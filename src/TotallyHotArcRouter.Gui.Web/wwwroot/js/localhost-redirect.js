// Redirects the dashboard from an IP-literal loopback address to the name "localhost", on the same port.
// ADR-0020: WebAuthn passkeys are scoped to the relying-party id "localhost" and the single origin
// https://localhost:<web port>, and a browser will not run a ceremony for an origin served from
// https://127.0.0.1 or https://[::1]. Loaded synchronously from <head>, ahead of the Blazor bootstrap, so
// the IP-literal origin never loads the app or takes the ADR-0012 session cookie it would then orphan.
(function () {
    // location.hostname keeps the brackets on an IPv6 literal ("[::1]"); accept the bare form as well.
    var host = window.location.hostname;
    if (host !== '127.0.0.1' && host !== '::1' && host !== '[::1]') return;

    var target = window.location.protocol + '//localhost' +
        (window.location.port ? ':' + window.location.port : '') +
        window.location.pathname + window.location.search + window.location.hash;
    window.location.replace(target);
})();
