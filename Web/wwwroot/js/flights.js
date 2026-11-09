// Flight lists: the "Recent flights" card on index.html (last 5) and the full history on flights.html.
// Both call GET api/flights?limit=&offset=, which returns { flights, totals, offset, limit }.
// Content loads when js/auth.js reports a signed-in user ("auth:signed-in"), and is hidden on sign-out.
//
// A section opts in with an id below. Inside it:
//   [data-flights-table]   where the table goes
//   [data-flights-status]  loading / empty / error text
//   [data-flights-more]    optional "Load more" button (paged history)
//   [data-flights-totals]  optional <dl> for the totals tiles

(() => {
    "use strict";

    const SECTIONS = [
        { id: "recent-flights", pageSize: 5, paged: false },
        { id: "flight-history", pageSize: 50, paged: true },
    ];

    // ---- Formatting ---------------------------------------------------------------------------

    function formatDuration(totalSeconds) {
        const s = Math.max(0, Math.round(totalSeconds ?? 0));
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        return h > 0 ? `${h}h ${String(m).padStart(2, "0")}m` : m > 0 ? `${m}m` : `${s}s`;
    }

    const formatDistance = (nm) => nm == null ? "—" : `${nm.toFixed(1)} NM`;

    // An airport code if there is one, otherwise the position, otherwise a dash.
    function formatPlace(place) {
        if (place?.ident) return { text: place.ident, title: place.name ?? "" };
        if (place?.latitude != null) {
            const text = `${place.latitude.toFixed(4)}, ${place.longitude.toFixed(4)}`;
            return { text, title: "Not at an airport" };
        }
        return { text: "—", title: "Unknown" };
    }

    // "C172 N172SP" on the first line, "Speedbird 123" under it; the sim's full title as the tooltip.
    function formatAircraft(aircraft) {
        if (!aircraft) return { text: "—", sub: "", title: "Unknown" };
        const text = [aircraft.model ?? aircraft.type ?? aircraft.title, aircraft.registration].filter(Boolean).join(" ");
        const sub = [aircraft.airline, aircraft.flightNumber].filter(Boolean).join(" ");
        const title = [aircraft.title, aircraft.type].filter(Boolean).join(" · ");
        return { text: text || sub || "—", sub: text ? sub : "", title };
    }

    function formatWhen(iso) {
        const date = new Date(iso);
        return {
            date: date.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" }),
            time: date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }),
        };
    }

    // ---- Rendering (textContent only, so server data is never parsed as HTML) ----------------

    function cell(tag, className, text, title) {
        const node = document.createElement(tag);
        node.className = className;
        node.textContent = text;
        if (title) node.title = title;
        return node;
    }

    function createTable() {
        const wrapper = document.createElement("div");
        wrapper.className = "overflow-x-auto";
        const table = document.createElement("table");
        table.className = "w-full text-left text-sm";
        const head = document.createElement("thead");
        const row = document.createElement("tr");
        row.className = "border-b border-slate-800 text-xs uppercase tracking-wide text-slate-500";
        for (const [label, align] of [["Date", ""], ["From", ""], ["To", ""], ["Aircraft", ""], ["Duration", "text-right"], ["Distance", "text-right"]]) {
            const th = cell("th", `py-2 pr-4 font-medium last:pr-0 ${align}`, label);
            th.scope = "col";
            row.append(th);
        }
        head.append(row);
        const body = document.createElement("tbody");
        table.append(head, body);
        wrapper.append(table);
        return { wrapper, body };
    }

    function flightRow(flight) {
        const row = document.createElement("tr");
        row.className = "border-b border-slate-800/60 last:border-0";

        const when = formatWhen(flight.startedUtc);
        const date = cell("td", "py-2 pr-4 whitespace-nowrap", when.date, new Date(flight.startedUtc).toLocaleString());
        date.append(cell("span", "ml-2 text-xs text-slate-500", when.time));

        const from = formatPlace(flight.departure);
        const to = formatPlace(flight.arrival);
        const aircraft = formatAircraft(flight.aircraft);
        const plane = cell("td", "py-2 pr-4 whitespace-nowrap", aircraft.text, aircraft.title);
        if (aircraft.sub) plane.append(cell("span", "ml-2 text-xs text-slate-500", aircraft.sub));
        row.append(
            date,
            cell("td", "py-2 pr-4 font-mono whitespace-nowrap", from.text, from.title),
            cell("td", "py-2 pr-4 font-mono whitespace-nowrap", to.text, to.title),
            plane,
            cell("td", "py-2 pr-4 text-right tabular-nums whitespace-nowrap", formatDuration(flight.durationSeconds)),
            cell("td", "py-2 text-right tabular-nums whitespace-nowrap", formatDistance(flight.distanceNm)));
        return row;
    }

    function renderTotals(target, totals) {
        if (!target) return;
        const tiles = [
            ["Flights", String(totals.flights)],
            ["Distance", formatDistance(totals.distanceNm)],
            ["Time", formatDuration(totals.durationSeconds)],
        ];
        target.replaceChildren(...tiles.map(([label, value]) => {
            const tile = document.createElement("div");
            tile.className = "rounded-xl border border-slate-800 bg-slate-900 p-4";
            tile.append(cell("dt", "text-xs uppercase tracking-wide text-slate-500", label),
                        cell("dd", "mt-1 text-2xl font-semibold tabular-nums", value));
            return tile;
        }));
    }

    // ---- Loading ------------------------------------------------------------------------------

    function setup({ id, pageSize, paged }) {
        const section = document.getElementById(id);
        if (!section) return null;

        const tableHost = section.querySelector("[data-flights-table]");
        const status = section.querySelector("[data-flights-status]");
        const more = section.querySelector("[data-flights-more]");
        const totalsHost = document.querySelector(`[data-flights-totals="${id}"]`);
        let table = null;
        let loaded = 0;
        let total = 0;
        let generation = 0;   // ignores responses that arrive after a sign-out or reload

        const setStatus = (text, error = false) => {
            status.textContent = text;
            status.className = `text-sm ${error ? "text-rose-400" : "text-slate-400"}`;
        };

        async function loadPage() {
            const mine = generation;
            if (more) more.disabled = true;
            setStatus(loaded === 0 ? "Loading flights…" : "Loading more…");
            try {
                const response = await fetch(`api/flights?limit=${pageSize}&offset=${loaded}`, {
                    headers: { Accept: "application/json" },
                    cache: "no-store",
                    credentials: "same-origin",
                });
                if (mine !== generation) return;
                if (response.status === 401) { setStatus("Your session has ended. Please sign in again.", true); return; }
                if (!response.ok) { setStatus(`Couldn't load flights (HTTP ${response.status}).`, true); return; }

                const data = await response.json();
                if (mine !== generation) return;
                total = data.totals.flights;
                renderTotals(totalsHost, data.totals);

                if (!table) {
                    table = createTable();
                    tableHost.replaceChildren(table.wrapper);
                }
                table.body.append(...data.flights.map(flightRow));
                loaded += data.flights.length;

                if (total === 0) {
                    tableHost.replaceChildren();
                    table = null;
                    setStatus("No flights yet. Flights recorded with the MSFS Telemetry companion app appear here once uploaded.");
                } else if (paged) {
                    setStatus(`Showing ${loaded} of ${total} flight${total === 1 ? "" : "s"}.`);
                } else {
                    setStatus("");
                }
                if (more) more.hidden = !paged || loaded >= total;
            } catch (err) {
                if (mine === generation) setStatus(`Couldn't load flights: ${err.message}`, true);
            } finally {
                if (more) more.disabled = false;
            }
        }

        function reset() {
            generation++;
            table = null;
            loaded = 0;
            total = 0;
            tableHost.replaceChildren();
            totalsHost?.replaceChildren();
            setStatus("");
            if (more) more.hidden = true;
        }

        more?.addEventListener("click", loadPage);
        return {
            signedIn() { reset(); section.hidden = false; if (totalsHost) totalsHost.hidden = false; loadPage(); },
            signedOut() { reset(); section.hidden = true; if (totalsHost) totalsHost.hidden = true; },
        };
    }

    const lists = SECTIONS.map(setup).filter(Boolean);
    document.addEventListener("auth:signed-in", () => lists.forEach((list) => list.signedIn()));
    document.addEventListener("auth:signed-out", () => lists.forEach((list) => list.signedOut()));
})();
