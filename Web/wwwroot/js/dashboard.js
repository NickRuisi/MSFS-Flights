// Dashboard page (dashboard.html): calls GET /api/status and renders the result.
//
// The URL is relative ("api/status", no leading slash), so it still works when IIS hosts the
// site as a sub-application (e.g. http://server/telemetry/). Keep the trailing slash on such URLs.

(() => {
    "use strict";

    const STATUS_URL = "api/status";
    const TIMEOUT_MS = 8000;

    const el = (id) => document.getElementById(id);

    function setBanner(state, text, detail = "") {
        const colors = { ok: "bg-emerald-500", degraded: "bg-amber-500", error: "bg-rose-500", pending: "bg-slate-500" };
        el("banner-dot").className = `h-3 w-3 rounded-full ${colors[state] ?? colors.pending}`;
        el("banner-text").textContent = text;
        el("banner-detail").textContent = detail;
    }

    // Render [label, value] pairs into a <dl>. textContent is used so server data is never parsed as HTML.
    function renderList(target, rows) {
        target.replaceChildren(...rows.map(([label, value]) => {
            const row = document.createElement("div");
            row.className = "flex justify-between gap-4";
            const dt = document.createElement("dt");
            dt.className = "text-slate-400";
            dt.textContent = label;
            const dd = document.createElement("dd");
            dd.className = "text-right font-mono break-all";
            dd.textContent = value ?? "—";
            row.append(dt, dd);
            return row;
        }));
    }

    const formatBytes = (n) => n == null ? null : n < 1024 ? `${n} B` : `${(n / 1024).toFixed(1)} KB`;

    async function refresh() {
        const button = el("refresh");
        button.disabled = true;
        setBanner("pending", "Contacting backend…");
        const started = performance.now();

        try {
            const response = await fetch(STATUS_URL, {
                headers: { Accept: "application/json" },
                cache: "no-store",
                signal: AbortSignal.timeout(TIMEOUT_MS),
            });
            const roundTrip = Math.round(performance.now() - started);

            // 503 still carries a JSON body describing the database problem.
            const body = await response.json();
            el("raw").textContent = JSON.stringify(body, null, 2);

            renderList(el("app-info"), [
                ["Name", body.app?.name],
                ["Version", body.app?.version],
                ["Environment", body.app?.environment],
                ["Host", body.app?.host],
                ["Runtime", body.app?.framework],
                ["Uptime", body.app ? `${body.app.uptimeSeconds}s` : null],
            ]);
            renderList(el("db-info"), [
                ["Connected", body.database?.connected ? "yes" : "no"],
                ["SQLite", body.database?.sqliteVersion],
                ["Journal mode", body.database?.journalMode],
                ["Tables", body.database?.tableCount?.toString()],
                ["File size", formatBytes(body.database?.fileSizeBytes)],
                ["Query latency", body.database ? `${body.database.latencyMs} ms` : null],
                ["Path", body.database?.path],
            ]);

            if (response.ok) {
                setBanner("ok", "Backend and database are responding", `Round trip ${roundTrip} ms · HTTP ${response.status}`);
            } else {
                setBanner("degraded", "Backend is up, but the database is not responding",
                    body.database?.error ?? `HTTP ${response.status}`);
            }
        } catch (err) {
            const reason = err.name === "TimeoutError" ? `No response within ${TIMEOUT_MS / 1000}s` : err.message;
            setBanner("error", "Could not reach the backend", reason);
            el("raw").textContent = String(err);
        } finally {
            button.disabled = false;
        }
    }

    el("refresh").addEventListener("click", refresh);
    refresh();
})();
