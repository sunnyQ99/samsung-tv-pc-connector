using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using TVCitation.Control;

internal static class PlayerPlaybackChecks
{
    private const string Probe = """
        (() => {
          const metrics = window.__playerCheck = {created:0,revoked:0,cancelled:0,released:0,removes:0,playing:0,statusActive:0,statusMax:0,statusAborted:0,maxBuffer:0,maxLag:0,seekInjected:false,playCalls:0,playRejected:0,seeking:0,errors:[]};
          const originalPlay=HTMLMediaElement.prototype.play;
          HTMLMediaElement.prototype.play=function(...args){metrics.playCalls++;const result=originalPlay.apply(this,args);result?.catch(error=>{metrics.playRejected++;metrics.lastPlayError=error.name;});return result;};
          const create = URL.createObjectURL, revoke = URL.revokeObjectURL;
          URL.createObjectURL = function(...args) { metrics.created++; return create.apply(this,args); };
          URL.revokeObjectURL = function(...args) { metrics.revoked++; return revoke.apply(this,args); };
          const remove = SourceBuffer.prototype.remove;
          SourceBuffer.prototype.remove = function(...args) {metrics.removes++;return remove.apply(this,args)};
          const cancel = ReadableStreamDefaultReader.prototype.cancel, release = ReadableStreamDefaultReader.prototype.releaseLock;
          ReadableStreamDefaultReader.prototype.cancel = function(...args) {metrics.cancelled++;return cancel.apply(this,args)};
          ReadableStreamDefaultReader.prototype.releaseLock = function(...args) {metrics.released++;return release.apply(this,args)};
          const originalFetch = fetch;
          window.fetch = async function(input, options) {
            if(input !== '/status') return originalFetch(input,options);
            metrics.statusActive++;metrics.statusMax=Math.max(metrics.statusMax,metrics.statusActive);
            try {return await originalFetch(input,options)} catch(error) {if(error.name==='AbortError')metrics.statusAborted++;throw error}
            finally {metrics.statusActive--}
          };
          document.addEventListener('DOMContentLoaded', () => {
            const video=document.querySelector('video');
            video.addEventListener('playing',()=>metrics.playing++);
            video.addEventListener('seeking',()=>metrics.seeking++);
            video.addEventListener('error',()=>metrics.errors.push(video.error?.message||'video error'));
            setInterval(()=>{
              if(video.buffered.length) {
                const end=video.buffered.end(video.buffered.length-1);
                metrics.maxBuffer=Math.max(metrics.maxBuffer,end-video.buffered.start(0));
                if(!video.paused && !video.seeking) metrics.maxLag=Math.max(metrics.maxLag,end-video.currentTime);
                if(!metrics.seekInjected && video.currentTime>6) {metrics.seekInjected=true;video.currentTime-=2;}
                metrics.currentLag=end-video.currentTime;
              }
              metrics.overlay=document.getElementById('status').style.display;
              metrics.width=video.videoWidth;metrics.height=video.videoHeight;
              metrics.paused=video.paused;metrics.readyState=video.readyState;metrics.muted=video.muted;metrics.playbackRate=video.playbackRate;
              chrome.webview.postMessage({type:'playbackCheck',...metrics});
            },200);
          });
        })();
        """;

    internal static void Run(string output, string? autoplayProbe = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "backend", "player.js"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Workspace root not found.");
        var fixture = File.ReadAllBytes(Path.Combine(root.FullName, "diagnostics/cast/player-test-1080p30.mp4"));
        var fragments = SplitFragments(fixture);
        using var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var elapsed = Stopwatch.StartNew();
        var requests = new ConcurrentDictionary<string, int>();
        var requestErrors = new ConcurrentQueue<string>();
        int delayedStatus = 0, available = 1;
        var handlers = new ConcurrentBag<Task>();
        var accepting = Task.Run(async () => {
            try {
                while (!cancellation.IsCancellationRequested) {
                    var context = await server.GetContextAsync().WaitAsync(cancellation.Token);
                    handlers.Add(Task.Run(async () => {
                        var path = context.Request.Url!.AbsolutePath;
                        var sequence = requests.AddOrUpdate(path, 1, (_, count) => count + 1);
                        try {
                            if (context.Request.Headers["X-TVCitation-Token"] != "local-playback-test") throw new InvalidOperationException("Missing auth: " + path);
                            switch(path) {
                                case "/player":
                                    var html=File.ReadAllText(Path.Combine(root.FullName,"backend/player.html"));
                                    if(autoplayProbe=="muted")html=html.Replace("<video id=", "<video muted id=");
                                    if(autoplayProbe=="default")html=html.Replace(" autoplay muted", " autoplay");
                                    await Reply(context, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"); break;
                                case "/player.js":
                                    var script=File.ReadAllText(Path.Combine(root.FullName,"backend/player.js"));
                                    // Preserve the legacy unmuted negative control in the local mock only.
                                    if(autoplayProbe=="default")script=script.Replace("video.defaultMuted = true;", "video.defaultMuted = false;").Replace("video.muted = true;", "video.muted = false;");
                                    await Reply(context,Encoding.UTF8.GetBytes(script),"text/javascript; charset=utf-8"); break;
                                case "/status":
                                    if(elapsed.Elapsed.TotalSeconds>22 && Interlocked.Exchange(ref delayedStatus,1)==0) await Task.Delay(4800,cancellation.Token);
                                    await Reply(context, JsonSerializer.SerializeToUtf8Bytes(new {controlConnected=true,videoConnected=Volatile.Read(ref available)!=0,videoState="Synthetic local H264 only"}), "application/json"); break;
                                case "/video":
                                    context.Response.ContentType="video/mp4";context.Response.Headers["X-Video-Mime"]="video/mp4; codecs=\"avc1.4D4028\"";context.Response.SendChunked=true;
                                    var pace=Stopwatch.StartNew();
                                    for(int index=0;index<fragments.Count;index++) {
                                        if(sequence==1 && index>120) break; // Actual EOF forces reconnect after trimming has started.
                                        var target=Math.Max(0,index-1)*100;
                                        var delay=target-pace.ElapsedMilliseconds;
                                        if(delay>0) await Task.Delay((int)delay,cancellation.Token);
                                        await context.Response.OutputStream.WriteAsync(fragments[index],cancellation.Token);
                                        await context.Response.OutputStream.FlushAsync(cancellation.Token);
                                    }
                                    context.Response.Close();break;
                                default: context.Response.StatusCode=404;context.Response.Close();break;
                            }
                        } catch(Exception error) when(error is HttpListenerException or IOException or OperationCanceledException) {try{context.Response.Abort();}catch{}}
                        catch(Exception error) {requestErrors.Enqueue(error.ToString());try{context.Response.Abort();}catch{}}
                    },cancellation.Token));
                }
            } catch(Exception error) when(error is HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
        });
        var samples=new List<JsonElement>();var fps=new List<double>();Exception? failure=null;
        using var form=new Form {Size=new Size(960,600),Location=new Point(-32000,-32000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false};
        using var browser=new WebView2 {Dock=DockStyle.Fill}; form.Controls.Add(browser);
        using var timer=new System.Windows.Forms.Timer {Interval=200};
        var dataDirectory=Path.Combine(output,"webview-playback-"+Guid.NewGuid().ToString("N"));
        form.Shown+=async(_,_)=>{
            try {
                // Match production autoplay policy. The remaining flags only keep
                // this deliberately offscreen test window from being throttled.
                var flags="--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
                var environment=await CoreWebView2Environment.CreateAsync(null,dataDirectory,new CoreWebView2EnvironmentOptions(flags));
                await browser.EnsureCoreWebView2Async(environment);
                browser.CoreWebView2.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                browser.CoreWebView2.WebResourceRequested+=(_,args)=>{
                    if(args.Request.Uri.StartsWith($"http://127.0.0.1:{port}/",StringComparison.Ordinal)) args.Request.Headers.SetHeader("X-TVCitation-Token","local-playback-test");
                    else if(!args.Request.Uri.StartsWith("blob:",StringComparison.Ordinal)) args.Response=environment.CreateWebResourceResponse(null,403,"Forbidden","");
                };
                browser.CoreWebView2.WebMessageReceived+=(_,args)=>{
                    using var json=JsonDocument.Parse(args.WebMessageAsJson);var value=json.RootElement;
                    var type=value.GetProperty("type").GetString();
                    if(type=="playbackCheck")samples.Add(value.Clone());
                    if(type=="videoStats")fps.Add(value.GetProperty("fps").GetDouble());
                };
                await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Probe);
                await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(PlayerInputBridge.Script);
                browser.CoreWebView2.Navigate($"http://127.0.0.1:{port}/player");
                timer.Start();
            }catch(Exception error){failure=error;form.Close();}
        };
        timer.Tick+=(_,_)=>{
            if(elapsed.Elapsed.TotalSeconds>(autoplayProbe is null ? 30 : 12))Interlocked.Exchange(ref available,0);
            if(elapsed.Elapsed.TotalSeconds>(autoplayProbe is null ? 33 : 14))form.Close();
        };
        try {
            Application.Run(form);
            if(failure is not null)throw failure;
            if(samples.Count==0)throw new InvalidOperationException("No WebView2 playback metrics.");
            var last=samples[^1];
            var positiveFps=fps.Where(value=>value>1).ToArray();
            var report=new {fixture="Synthetic 1920x1080 H264 30fps; no TV access",autoplayProbe,samples=samples.Count,requests=requests.ToDictionary(),last,
                measuredPresentedFps=positiveFps,maximumBufferSeconds=samples.Max(value=>value.GetProperty("maxBuffer").GetDouble()),errors=requestErrors.ToArray()};
            File.WriteAllText(Path.Combine(output,"playback-result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            if(autoplayProbe is not null) {Console.WriteLine("Autoplay probe completed: "+autoplayProbe+"; see playback-result.json");return;}
            Require(requestErrors.IsEmpty,"Loopback request error.");
            Require(requests.GetValueOrDefault("/player.js")>=1,"External authenticated player.js did not load.");
            Require(requests.GetValueOrDefault("/video")>=3,"EOF/status-timeout reconnect missing.");
            Require(samples.Any(value=>value.GetProperty("width").GetInt32()==1920 && value.GetProperty("height").GetInt32()==1080),"Actual 1080p decode missing.");
            Require(positiveFps.Length>=5 && positiveFps.Average()>10,"No measured compositor frame flow.");
            Require(last.GetProperty("playRejected").GetInt32()==0,"Production autoplay policy rejected playback.");
            Require(last.GetProperty("removes").GetInt32()>0,"Old MSE ranges were not trimmed.");
            Require(report.maximumBufferSeconds<=10.2,"MSE buffer exceeded bounded window.");
            Require(last.GetProperty("statusMax").GetInt32()==1 && last.GetProperty("statusAborted").GetInt32()>=1,"Status polling overlap or missing 4-second cancellation.");
            Require(last.GetProperty("created").GetInt32()==last.GetProperty("revoked").GetInt32(),"Object URLs leaked after stop.");
            Require(last.GetProperty("cancelled").GetInt32()>=3 && last.GetProperty("released").GetInt32()>=3,"Stream readers not cancelled/released.");
            Require(last.GetProperty("overlay").GetString()!="none","Offline overlay hidden without playing.");
            Require(last.GetProperty("errors").GetArrayLength()==0,"Video decode error.");
            Console.WriteLine("PASS: real WebView2 1080p30 fMP4 decode, measured presented frames, bounded MSE trim, EOF/status-timeout reconnect, reader/URL cleanup and authenticated script route.");
        }finally {
            cancellation.Cancel();server.Stop();try{accepting.GetAwaiter().GetResult();}catch{}
            try{Task.WhenAll(handlers).Wait(TimeSpan.FromSeconds(2));}catch{}
        }
    }

    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static async Task Reply(HttpListenerContext context,byte[] body,string type){context.Response.ContentType=type;context.Response.ContentLength64=body.Length;await context.Response.OutputStream.WriteAsync(body);context.Response.Close();}
    private static List<byte[]> SplitFragments(byte[] bytes)
    {
        var fragments=new List<byte[]>();int start=0,offset=0;
        while(offset<bytes.Length){int size=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset,4)));if(size<8||offset+size>bytes.Length)throw new InvalidDataException("Bad fMP4 fixture.");
            string type=Encoding.ASCII.GetString(bytes,offset+4,4);if(type=="moof"&&offset>start){fragments.Add(bytes[start..offset]);start=offset;}offset+=size;}
        fragments.Add(bytes[start..]);return fragments;
    }
}
