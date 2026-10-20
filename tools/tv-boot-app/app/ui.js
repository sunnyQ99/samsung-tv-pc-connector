/* This UI only starts its own package's health service; it does not certify boot startup. */
window.addEventListener('load', function () {
  var state = document.getElementById('state');
  try {
    tizen.application.launch('TVCitBoot1.Service', function () {
      state.textContent = 'The connection service was asked to start. The PC will verify the connection.';
    }, function () { state.textContent = 'The connection service did not start. Check the status on the PC.'; });
  } catch (_) { state.textContent = 'The connection service did not start. Check the status on the PC.'; }
});
