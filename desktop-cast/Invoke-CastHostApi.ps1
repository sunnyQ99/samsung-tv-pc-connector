param(
    [ValidateSet('GET', 'POST', 'DELETE')][string]$Method = 'GET',
    [ValidatePattern('^/api/[a-zA-Z0-9/_-]+$')][string]$Path = '/api/apps',
    [hashtable]$Body
)
$ErrorActionPreference = 'Stop'
$castPrivate = Join-Path $env:LOCALAPPDATA 'TVCITATION\Sunshine'
$castCredential = Import-Clixml -LiteralPath (Join-Path $castPrivate 'web-credential.clixml')
$castCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem([IO.File]::ReadAllText((Join-Path $castPrivate 'cacert.pem')))
$castExpectedHash = $castCertificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
if (-not ('TVCitation.PinnedHttpClient' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Security.Cryptography;
namespace TVCitation {
    public static class PinnedHttpClient {
        public static HttpClient Create(string expectedHash) {
            var handler = new HttpClientHandler();
            handler.UseProxy = false;
            handler.AllowAutoRedirect = false;
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                certificate != null && String.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), expectedHash, StringComparison.OrdinalIgnoreCase);
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        }
    }
}
'@
}
$castClient = [TVCitation.PinnedHttpClient]::Create($castExpectedHash)
try {
    $castBasic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($castCredential.UserName + ':' + $castCredential.GetNetworkCredential().Password))
    $castClient.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Basic', $castBasic)
    $castClient.DefaultRequestHeaders.Add('Origin', 'https://localhost:47990')
    $castRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), ('https://localhost:47990' + $Path))
    if ($null -ne $Body) {
        $castRequest.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 10 -Compress), [Text.Encoding]::UTF8, 'application/json')
    }
    $castResponse = $castClient.SendAsync($castRequest).GetAwaiter().GetResult()
    $castContent = $castResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $castResponse.IsSuccessStatusCode) {
        throw "Sunshine local API returned HTTP $([int]$castResponse.StatusCode)."
    }
    if ($castContent) { $castContent | ConvertFrom-Json } else { [pscustomobject]@{ Status = [int]$castResponse.StatusCode } }
} finally {
    $castClient.Dispose()
    $castCredential = $null
    $castBasic = $null
}
