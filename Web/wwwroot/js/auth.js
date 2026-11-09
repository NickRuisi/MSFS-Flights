// "Sign in / create profile with Google" and the profile editor.
// Loaded on every page, after any page script that listens for its "auth:signed-in" /
// "auth:signed-out" events. The sign-in header is on all pages; the profile form and the signed-out
// intro exist only on index.html, so the code skips them when they're missing.
//
// 1. GET api/auth/config gives the Google client ID (kept in appsettings so it lives in one place).
// 2. GET api/auth/me: 200 means a session cookie is already valid; 401 means show Google's button.
// 3. Google Identity Services calls onGoogleCredential with a signed ID token. We POST it to
//    api/auth/google. The server verifies it, creates the profile on first sign-in, and sets an
//    HttpOnly session cookie. The token is never stored in the browser.
//
// URLs are relative (no leading slash) so the app also works as an IIS sub-application.

(() => {
    "use strict";

    const GIS_SRC = "https://accounts.google.com/gsi/client";
    const NEW_USER_PARAM = "new";
    const el = (id) => document.getElementById(id);
    const show = (node, visible) => { if (node) node.hidden = !visible; }; // tolerates missing elements

    let googleReady = null; // Promise for the GIS script, loaded once on first need.

    // ---- HTTP -------------------------------------------------------------------------------

    async function api(method, url, body) {
        const response = await fetch(url, {
            method,
            headers: body === undefined
                ? { Accept: "application/json" }
                : { Accept: "application/json", "Content-Type": "application/json" },
            body: body === undefined ? undefined : JSON.stringify(body),
            cache: "no-store",
            credentials: "same-origin",
        });
        const text = await response.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { /* non-JSON error page */ }
        return { ok: response.ok, status: response.status, data };
    }

    const problemMessage = (res, fallback) => res.data?.detail ?? res.data?.title ?? `${fallback} (HTTP ${res.status})`;

    // ---- Signed-out state: Google button ------------------------------------------------------

    function loadGoogleScript() {
        googleReady ??= new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = GIS_SRC;
            script.async = true;
            script.onload = resolve;
            script.onerror = () => { googleReady = null; reject(new Error("Couldn't load Google sign-in. Check your connection or content blockers.")); };
            document.head.append(script);
        });
        return googleReady;
    }

    function showAuthError(message) {
        const node = el("auth-error");
        node.textContent = message ?? "";
        show(node, Boolean(message));
    }

    async function showSignedOut(config) {
        el("auth-loading").hidden = true;
        show(el("auth-signed-in"), false);
        show(el("profile"), false);
        show(el("api-key"), false);
        setApiKey(null);
        document.dispatchEvent(new CustomEvent("auth:signed-out"));
        show(el("signed-out-intro"), true);
        show(el("auth-signed-out"), true);

        if (!config?.googleEnabled) {
            el("google-button").replaceChildren();
            showAuthError("Google sign-in isn't configured yet (Authentication:Google:ClientId).");
            return;
        }

        try {
            await loadGoogleScript();
        } catch (err) {
            showAuthError(err.message);
            return;
        }

        google.accounts.id.initialize({
            client_id: config.googleClientId,
            callback: onGoogleCredential,
            ux_mode: "popup",
            cancel_on_tap_outside: true,
        });
        google.accounts.id.renderButton(el("google-button"), {
            type: "standard",
            theme: "filled_black",
            size: "large",
            shape: "pill",
            text: "continue_with", // "Continue with Google": covers both sign-in and new profiles
            logo_alignment: "left",
        });
    }

    async function onGoogleCredential(response) {
        showAuthError(null);
        const res = await api("POST", "api/auth/google", { credential: response.credential });
        if (!res.ok) {
            showAuthError(problemMessage(res, "Sign-in failed"));
            return;
        }
        // A brand-new user who signed in from another page goes to the profile page to finish setting up.
        if (res.data.isNewUser && !el("profile-form")) {
            window.location.href = `./?${NEW_USER_PARAM}=1`;
            return;
        }
        showSignedIn(res.data.user, res.data.isNewUser);
    }

    // ---- Signed-in state: header + profile form -----------------------------------------------

    function showSignedIn(user, isNewUser = false) {
        el("auth-loading").hidden = true;
        show(el("auth-signed-out"), false);
        show(el("auth-signed-in"), true);
        show(el("signed-out-intro"), false);
        show(el("profile"), true);
        show(el("welcome"), isNewUser);

        renderHeader(user);
        if (el("profile-form")) fillForm(user, isNewUser);
        if (el("api-key")) {
            show(el("api-key"), true);
            loadApiKey();
        }
        // Other page scripts (js/flights.js) load their signed-in content on this.
        document.dispatchEvent(new CustomEvent("auth:signed-in", { detail: user }));
    }

    function renderHeader(user) {
        el("auth-name").textContent = user.displayName;
        el("auth-email").textContent = user.email;
        const avatar = el("auth-avatar");
        if (user.avatarUrl) {
            avatar.src = user.avatarUrl;
            show(avatar, true);
        } else {
            avatar.removeAttribute("src");
            show(avatar, false);
        }
    }

    function fillForm(user, isNewUser) {
        const form = el("profile-form");
        form.elements.displayName.value = user.displayName ?? "";
        form.elements.email.value = user.email ?? "";
        form.elements.homeAirportIcao.value = user.homeAirportIcao ?? "";
        form.elements.bio.value = user.bio ?? "";

        // Suggest the browser's time zone to new users who haven't set one; it's saved only when they click Save.
        let timeZone = user.timeZone ?? "";
        if (!timeZone && isNewUser) {
            try { timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone ?? ""; } catch { /* leave blank */ }
        }
        form.elements.timeZone.value = timeZone;

        clearFieldErrors();
        setProfileStatus("");
        const created = new Date(user.createdUtc).toLocaleDateString();
        el("profile-meta").textContent = `Member since ${created} · ${user.loginCount} sign-in${user.loginCount === 1 ? "" : "s"}`;
    }

    function clearFieldErrors() {
        document.querySelectorAll("[data-error-for]").forEach((node) => { node.textContent = ""; });
    }

    function setProfileStatus(text, kind = "info") {
        const node = el("profile-status");
        node.textContent = text;
        node.className = `text-sm ${kind === "error" ? "text-rose-400" : kind === "ok" ? "text-emerald-400" : "text-slate-400"}`;
    }

    async function saveProfile(event) {
        event.preventDefault();
        const form = el("profile-form");
        const button = el("profile-save");
        clearFieldErrors();
        setProfileStatus("Saving…");
        button.disabled = true;

        try {
            const res = await api("PUT", "api/profile", {
                displayName: form.elements.displayName.value,
                homeAirportIcao: form.elements.homeAirportIcao.value,
                timeZone: form.elements.timeZone.value,
                bio: form.elements.bio.value,
            });

            if (res.ok) {
                renderHeader(res.data);
                fillForm(res.data, false);
                show(el("welcome"), false);
                setProfileStatus("Saved", "ok");
            } else if (res.status === 400 && res.data?.errors) {
                // ValidationProblem: { errors: { fieldName: [messages] } }
                for (const [field, messages] of Object.entries(res.data.errors)) {
                    const target = document.querySelector(`[data-error-for="${CSS.escape(field)}"]`);
                    if (target) target.textContent = messages.join(" ");
                }
                setProfileStatus("Please fix the highlighted fields.", "error");
            } else if (res.status === 401) {
                await start(); // session expired or account deactivated
            } else {
                setProfileStatus(problemMessage(res, "Couldn't save"), "error");
            }
        } catch (err) {
            setProfileStatus(err.message, "error");
        } finally {
            button.disabled = false;
        }
    }

    // ---- API key (index.html only) ------------------------------------------------------------
    // GET api/profile/api-key returns the key (creating it on first visit); POST replaces it.
    // The key is kept in this variable and masked in the box until the user clicks Show.

    let apiKey = null;        // the last response from the server, or null
    let apiKeyShown = false;

    function setApiKey(data, reveal = false) {
        apiKey = data;
        apiKeyShown = reveal && Boolean(data?.canShow);
        renderApiKey();
    }

    function renderApiKey() {
        const box = el("api-key-value");
        if (!box) return;
        const canShow = Boolean(apiKey?.canShow);

        box.value = !apiKey ? "…"
            : apiKeyShown ? apiKey.key
            : `${apiKey.prefix}${"•".repeat(24)}`;
        const showButton = el("api-key-show");
        showButton.textContent = apiKeyShown ? "Hide" : "Show";
        showButton.setAttribute("aria-pressed", String(apiKeyShown));
        showButton.disabled = !canShow;
        el("api-key-copy").disabled = !canShow;
        el("api-key-replace").disabled = !apiKey;

        if (!apiKey) {
            el("api-key-meta").textContent = "";
            return;
        }
        const created = new Date(apiKey.createdUtc).toLocaleString();
        const used = apiKey.lastUsedUtc ? `last used ${new Date(apiKey.lastUsedUtc).toLocaleString()}` : "not used yet";
        el("api-key-meta").textContent = `Created ${created} · ${used}`;
        if (!canShow) {
            setApiKeyStatus("This key can't be shown any more (the server's encryption keys changed). It still works; replace it to get one you can copy.", "error");
        }
    }

    function setApiKeyStatus(text, kind = "info") {
        const node = el("api-key-status");
        node.textContent = text;
        node.className = `text-sm ${kind === "error" ? "text-rose-400" : kind === "ok" ? "text-emerald-400" : "text-slate-400"}`;
    }

    async function loadApiKey() {
        setApiKeyStatus("");
        try {
            const res = await api("GET", "api/profile/api-key");
            if (res.ok) setApiKey(res.data);
            else if (res.status === 401) await start();
            else setApiKeyStatus(problemMessage(res, "Couldn't load the API key"), "error");
        } catch (err) {
            setApiKeyStatus(err.message, "error");
        }
    }

    async function replaceApiKey() {
        if (!confirm("Replace your API key?\n\nThe current key stops working right away. You'll need to put the new key into the companion app.")) return;
        const button = el("api-key-replace");
        button.disabled = true;
        setApiKeyStatus("Replacing…");
        try {
            const res = await api("POST", "api/profile/api-key");
            if (res.ok) {
                setApiKey(res.data, true);   // show the new key so it can be copied straight away
                setApiKeyStatus("New key created. Copy it into the companion app.", "ok");
            } else if (res.status === 401) {
                await start();
            } else {
                setApiKeyStatus(problemMessage(res, "Couldn't replace the key"), "error");
            }
        } catch (err) {
            setApiKeyStatus(err.message, "error");
        } finally {
            button.disabled = !apiKey;
        }
    }

    async function copyApiKey() {
        if (!apiKey?.canShow) return;
        try {
            // The Clipboard API needs a secure context (HTTPS or localhost). Over plain HTTP on IIS,
            // fall back to selecting the text in the box and copying it.
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(apiKey.key);
            } else {
                const box = el("api-key-value");
                box.value = apiKey.key;
                box.select();
                const copied = document.execCommand("copy");
                box.setSelectionRange(0, 0);
                renderApiKey();
                if (!copied) throw new Error("Your browser blocked copying. Click Show and copy the key by hand.");
            }
            setApiKeyStatus("Copied to the clipboard.", "ok");
        } catch (err) {
            setApiKeyStatus(err.message, "error");
        }
    }

    async function signOut() {
        await api("POST", "api/auth/logout");
        // Stop Google from silently picking the same account on the next visit.
        window.google?.accounts?.id?.disableAutoSelect();
        await start();
    }

    // ---- Startup ------------------------------------------------------------------------------

    // Reads the ?new=1 flag set by the redirect above, then removes it from the address bar
    // so a reload doesn't show the welcome note again.
    function consumeNewUserFlag() {
        const url = new URL(window.location.href);
        if (!url.searchParams.has(NEW_USER_PARAM)) return false;
        url.searchParams.delete(NEW_USER_PARAM);
        history.replaceState(null, "", url);
        return true;
    }

    async function start() {
        let config = null;
        try {
            const [configRes, meRes] = await Promise.all([api("GET", "api/auth/config"), api("GET", "api/auth/me")]);
            config = configRes.data;
            if (meRes.ok) {
                showSignedIn(meRes.data, consumeNewUserFlag());
                return;
            }
        } catch {
            // Backend unreachable: show the signed-out view. The dashboard page reports the details.
        }
        await showSignedOut(config);
    }

    el("profile-form")?.addEventListener("submit", saveProfile);
    el("api-key-show")?.addEventListener("click", () => { apiKeyShown = !apiKeyShown; renderApiKey(); });
    el("api-key-copy")?.addEventListener("click", copyApiKey);
    el("api-key-replace")?.addEventListener("click", replaceApiKey);
    el("sign-out").addEventListener("click", signOut);
    start();
})();
