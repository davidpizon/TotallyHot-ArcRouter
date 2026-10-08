// Backs WasmWebAuthnCeremony - the browser IWebAuthnCeremony implementation for ADR-0020's passkey content
// gate. The router (Fido2) issues the WebAuthn options as JSON and verifies the response; this file only
// moves bytes between that JSON (base64url strings) and navigator.credentials (ArrayBuffers). Every
// ceremony demands user verification regardless of what the options say - presence-only approval is not
// acceptable for the gate - and nothing here ever touches localStorage, sessionStorage, or cookies.
//
// A rejected promise surfaces in .NET as a JSException whose message is shown to the operator, so every
// failure an operator can act on is thrown here as an Error with a plain-language message.
window.webAuthnInterop = (function () {
    function base64UrlToBuffer(value) {
        var padded = value.replace(/-/g, '+').replace(/_/g, '/');
        while (padded.length % 4 !== 0) padded += '=';
        var binary = atob(padded);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes.buffer;
    }

    function bufferToBase64Url(buffer) {
        var bytes = new Uint8Array(buffer);
        var binary = '';
        for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    // Fido2's serializer writes unset members as null; the browser API wants them absent.
    function stripNulls(source) {
        var result = {};
        Object.keys(source || {}).forEach(function (key) {
            if (source[key] !== null && source[key] !== undefined) result[key] = source[key];
        });
        return result;
    }

    function toDescriptor(descriptor) {
        var result = { type: descriptor.type || 'public-key', id: base64UrlToBuffer(descriptor.id) };
        if (descriptor.transports) result.transports = descriptor.transports;
        return result;
    }

    // Passkeys are bound to the relying-party id "localhost" (ADR-0020), so a ceremony started from any
    // other host name can only fail inside the browser with an opaque SecurityError. Say why up front.
    function requireUsableContext() {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            throw new Error('This browser does not support passkeys (WebAuthn).');
        }
        if (!window.isSecureContext) {
            throw new Error('Passkeys need a secure context. Open the dashboard over https at localhost.');
        }
        if (location.hostname !== 'localhost') {
            throw new Error('Passkeys are bound to the name "localhost". Open the dashboard at https://localhost:' +
                location.port + ' instead of ' + location.hostname + '.');
        }
    }

    function describeFailure(error, creating) {
        var name = error && error.name;
        if (name === 'NotAllowedError' || name === 'AbortError') {
            return 'The passkey prompt was dismissed or timed out.';
        }
        if (name === 'InvalidStateError') {
            return creating
                ? 'This authenticator already holds a passkey for the Arc Router.'
                : 'No matching passkey is available on this device.';
        }
        if (name === 'SecurityError') {
            return 'The browser refused the passkey request for this address. Open the dashboard at https://localhost.';
        }
        if (name === 'NotSupportedError') {
            return 'This device cannot create a passkey that meets the required security level.';
        }
        return (error && error.message) ? error.message : 'The passkey request failed.';
    }

    async function create(optionsJson) {
        requireUsableContext();
        var options = JSON.parse(optionsJson);
        var selection = stripNulls(options.authenticatorSelection);
        selection.userVerification = 'required';

        var publicKey = {
            rp: stripNulls(options.rp),
            user: {
                id: base64UrlToBuffer(options.user.id),
                name: options.user.name,
                displayName: options.user.displayName
            },
            challenge: base64UrlToBuffer(options.challenge),
            pubKeyCredParams: options.pubKeyCredParams,
            authenticatorSelection: selection,
            attestation: 'none'
        };
        if (options.timeout) publicKey.timeout = options.timeout;
        if (options.excludeCredentials) publicKey.excludeCredentials = options.excludeCredentials.map(toDescriptor);

        var credential;
        try {
            credential = await navigator.credentials.create({ publicKey: publicKey });
        } catch (error) {
            throw new Error(describeFailure(error, true));
        }
        if (!credential) throw new Error('The passkey prompt returned no credential.');

        var response = credential.response;
        return JSON.stringify({
            id: credential.id,
            rawId: bufferToBase64Url(credential.rawId),
            type: credential.type,
            response: {
                attestationObject: bufferToBase64Url(response.attestationObject),
                clientDataJSON: bufferToBase64Url(response.clientDataJSON),
                transports: typeof response.getTransports === 'function' ? response.getTransports() : []
            },
            clientExtensionResults: credential.getClientExtensionResults()
        });
    }

    async function get(optionsJson) {
        requireUsableContext();
        var options = JSON.parse(optionsJson);

        var publicKey = {
            challenge: base64UrlToBuffer(options.challenge),
            userVerification: 'required'
        };
        if (options.rpId) publicKey.rpId = options.rpId;
        if (options.timeout) publicKey.timeout = options.timeout;
        if (options.allowCredentials) publicKey.allowCredentials = options.allowCredentials.map(toDescriptor);

        var credential;
        try {
            credential = await navigator.credentials.get({ publicKey: publicKey });
        } catch (error) {
            throw new Error(describeFailure(error, false));
        }
        if (!credential) throw new Error('The passkey prompt returned no credential.');

        var response = credential.response;
        return JSON.stringify({
            id: credential.id,
            rawId: bufferToBase64Url(credential.rawId),
            type: credential.type,
            response: {
                authenticatorData: bufferToBase64Url(response.authenticatorData),
                signature: bufferToBase64Url(response.signature),
                clientDataJSON: bufferToBase64Url(response.clientDataJSON),
                userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : null
            },
            clientExtensionResults: credential.getClientExtensionResults()
        });
    }

    return { create: create, get: get };
})();
