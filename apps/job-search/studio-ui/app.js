// Job Search Studio UI. No dependencies, no third-party requests. Every write carries the X-Studio-Request header
// (the server refuses writes without it), and every piece of text from the server is set as text, never as HTML.

const STATUSES = ["not_applied", "preparing", "applied", "interviewing", "offer", "rejected", "withdrawn", "not_pursuing"];
const ASSERTIONS = new Set(["applied", "interviewing", "offer", "rejected"]);
const USER_ONLY = {
  work_authorization: "Work authorization (e.g. US citizen, permanent resident, needs sponsorship)",
  requires_sponsorship: "Do you require visa sponsorship? (yes / no)",
  clearance_status: "Clearance status, in your words (e.g. active Secret, none)",
  willing_to_relocate: "Willing to relocate? (yes / no)",
  max_travel_pct: "Most travel you accept, in percent",
  earliest_start_date: "Earliest start date (YYYY-MM-DD)",
};

// ── tiny DOM builder: strings become text nodes, so server data can never become markup ─────────────────
function h(tag, attrs, ...children) {
  const el = document.createElementNS(tag === "svg" || attrs?.svg ? "http://www.w3.org/2000/svg" : "http://www.w3.org/1999/xhtml", tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === undefined || v === null || v === false || k === "svg") continue;
    if (k.startsWith("on") && typeof v === "function") el.addEventListener(k.slice(2), v);
    else if (k === "class") el.setAttribute("class", v);
    else if (k === "value") el.value = v;
    else if (k === "checked") el.checked = !!v;
    else el.setAttribute(k, v === true ? "" : String(v));
  }
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}
const $ = (sel, root = document) => root.querySelector(sel);
let fieldIds = 0;
/** A label tied to its control (for + id), so every control has an accessible name. */
function field(text, control, hint) {
  if (!control.id) control.id = "f" + ++fieldIds;
  return h("div", {}, h("label", { for: control.id }, text), control, hint ? h("div", { class: "hint" }, hint) : null);
}
function clear(el) { while (el.firstChild) el.firstChild.remove(); return el; }

// ── API ────────────────────────────────────────────────────────────────────────────────────────────────
class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
async function call(method, path, body) {
  const init = { method, headers: { Accept: "application/json" }, credentials: "same-origin" };
  if (method !== "GET") init.headers["X-Studio-Request"] = "1";
  if (body !== undefined) { init.headers["Content-Type"] = "application/json"; init.body = JSON.stringify(body); }
  const res = await fetch(path, init);
  const text = await res.text();
  const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
  if (!res.ok) throw new ApiError(res.status, (data && data.error) || res.statusText);
  return data;
}
const api = {
  get: (p) => call("GET", p),
  post: (p, b) => call("POST", p, b ?? {}),
  put: (p, b) => call("PUT", p, b),
};

function toast(message, error = false) {
  const t = $("#toast");
  t.textContent = message;
  t.className = "toast" + (error ? " error" : "");
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), error ? 7000 : 3500);
}
async function guard(work) {
  try { return await work(); }
  catch (e) {
    if (e.status === 401) { await boot(); return; }
    if (e.message === "consent-required") { location.hash = "#/welcome"; await boot(); return; }
    // Shown once, here; not rethrown, so a handled error never becomes an uncaught one.
    toast(e.message || "Something went wrong", true);
    return undefined;
  }
}

// ── app state and routing ──────────────────────────────────────────────────────────────────────────────
const state = { meta: null, me: null };

async function boot() {
  state.meta = await api.get("/api/meta");
  $("#synthetic-banner").hidden = !state.meta.synthetic;
  try { state.me = await api.get("/api/me"); }
  catch (e) { if (e.status !== 401) throw e; state.me = null; }
  renderWho();
  await route();
}

function renderWho() {
  const who = clear($("#who"));
  $("#nav").hidden = !(state.me && state.me.onboarded);
  if (!state.me) return;
  who.append(
    h("span", { "data-test": "signed-in-as" }, state.me.displayName),
    h("button", { class: "btn small ghost", "data-test": "sign-out", onclick: () => guard(async () => { await api.post("/auth/signout"); state.me = null; location.hash = ""; await boot(); }) }, "Sign out"),
  );
}

async function route() {
  const main = clear($("#main"));
  if (!state.me) return signIn(main);
  const path = location.hash.replace(/^#/, "") || "/designer";
  if (!state.me.onboarded) {
    // The notice must be readable before anyone agrees to it.
    if (path === "/privacy") { main.append(h("p", {}, h("a", { href: "#/welcome", "data-test": "back-to-welcome" }, "← Back"))); return privacy(main); }
    return welcome(main);
  }
  for (const a of document.querySelectorAll("#nav a")) a.setAttribute("aria-current", a.getAttribute("href") === "#" + path ? "page" : "false");
  const views = { "/designer": designer, "/tracker": tracker, "/runs": runs, "/profile": profile, "/privacy": privacy, "/welcome": designer };
  await (views[path] || designer)(main);
  main.focus({ preventScroll: true });
}
window.addEventListener("hashchange", () => guard(route));

// ── sign-in and first run ──────────────────────────────────────────────────────────────────────────────
function signIn(main) {
  const failed = new URLSearchParams(location.search).get("signin");
  main.append(h("section", { class: "hero" },
    h("h1", {}, "Your job search, designed by you."),
    h("p", {}, "Build the pipeline that reads job postings for you: add steps from a shared library, or describe a new one in plain words. Your documents stay in your own Google Drive."),
    h("ul", { class: "points" },
      h("li", {}, h("strong", {}, "Your personal data stays on this server."), "Anything about you is handled only by a model running here. New steps are written from generic descriptions with no personal details."),
      h("li", {}, h("strong", {}, "Steps that are proven safe."), "Every step is certified by Ashlar's gate and runs isolated, with no access to the network or your files."),
      h("li", {}, h("strong", {}, "Nothing is sent for you."), "The studio never applies, messages an employer, or changes sharing without your say-so."),
    ),
    failed ? h("p", { class: "badge bad" }, "Sign-in did not complete. Please try again.") : null,
    state.meta.googleSignIn ? h("p", {}, h("a", { class: "btn primary", href: "/auth/google/start", "data-test": "google-sign-in" }, "Sign in with Google")) : null,
    state.meta.synthetic ? h("div", { class: "panel" },
      h("h2", {}, "Sign in as a made-up persona"),
      h("p", { class: "hint" }, "This is a synthetic environment for testing. These people and their résumés are invented."),
      h("div", { class: "row" }, (state.meta.personas || []).map((p) =>
        h("button", { class: "btn", "data-test": `persona-${p.email}`, onclick: () => guard(async () => { await api.post("/auth/dev", { email: p.email }); await boot(); }) }, p.displayName))),
    ) : null,
    !state.meta.googleSignIn && !state.meta.synthetic ? h("p", { class: "hint" }, "Sign-in is not configured on this server yet.") : null,
  ));
}

function welcome(main) {
  const boxes = {
    "privacy-notice": h("input", { type: "checkbox", id: "c-notice", "data-test": "consent-privacy-notice" }),
    terms: h("input", { type: "checkbox", id: "c-terms", "data-test": "consent-terms" }),
    "age-16-or-over": h("input", { type: "checkbox", id: "c-age", "data-test": "consent-age" }),
  };
  const go = h("button", { class: "btn primary", "data-test": "consent-continue", disabled: true }, "Continue");
  const update = () => (go.disabled = !Object.values(boxes).every((b) => b.checked));
  Object.values(boxes).forEach((b) => b.addEventListener("change", update));
  go.addEventListener("click", () => guard(async () => {
    await api.post("/api/me/consent", { kinds: Object.keys(boxes), timezone: Intl.DateTimeFormat().resolvedOptions().timeZone });
    location.hash = "#/profile";
    await boot();
  }));
  main.append(h("section", { class: "hero" },
    h("h1", {}, `Welcome, ${state.me.displayName}`),
    h("p", {}, "Before the studio stores or reads anything about you, please read how it handles your data."),
    noticeSummary(),
    h("div", { class: "panel" },
      h("label", { class: "check", for: "c-notice" }, boxes["privacy-notice"], h("span", {}, "I have read the privacy notice (", h("a", { href: "#/privacy", "data-test": "read-notice" }, "read it"), ").")),
      h("label", { class: "check", for: "c-terms" }, boxes.terms, h("span", {}, "I agree to the terms of use.")),
      h("label", { class: "check", for: "c-age" }, boxes["age-16-or-over"], h("span", {}, "I am 16 or older.")),
      h("div", { class: "row" }, go),
    ),
  ));
}

function noticeSummary() {
  return h("ul", { class: "points" },
    h("li", {}, h("strong", {}, "What we keep"), "Your résumé, the profile you confirm, your preferences, your workflow, and the jobs we find for you. Drafts and PDFs are files in your own Google Drive."),
    h("li", {}, h("strong", {}, "Where it goes"), "Personal data goes only to the model on this server. Google stores the files you ask us to create in your Drive. Claude (Anthropic) receives only generic step designs with no personal details."),
    h("li", {}, h("strong", {}, "Your rights"), "Download everything we hold, or delete your account and all of it, at any time from Privacy & data."),
  );
}

// ── designer ───────────────────────────────────────────────────────────────────────────────────────────
async function designer(main) {
  const [workflow, prefs, forge] = await Promise.all([api.get("/api/workflow"), api.get("/api/preferences"), api.get("/api/forge")]);
  let steps = workflow.steps.map((s) => ({ ...s, settings: [...s.settings] }));
  let dirty = false;
  const markDirty = () => { dirty = true; drawSave(); };

  const pipeline = h("div", { class: "pipeline", "data-test": "pipeline" });
  const savebar = h("div", { class: "savebar", hidden: true });
  const side = h("aside", { class: "side" });
  const wiringBox = h("div");
  main.append(
    h("div", { class: "row spread" },
      h("div", {}, h("h1", {}, "Your job search pipeline"),
        h("p", { class: "hint" }, "Every posting found for you runs through these steps, top to bottom. Green steps are built in and certified; blue ones are yours.")),
      h("div", { class: "chips" },
        h("span", { class: "badge private", title: "Handles anything about you" }, "Private model: ", forge.privateModel),
        h("span", { class: "badge", title: "Writes step code from generic designs only" }, "Step writer: ", forge.writer),
        h("span", { class: "badge accent", "data-test": "allowance" }, `New steps this month: ${forge.used} of ${forge.limit}`))),
    h("div", { class: "designer" }, h("div", {}, pipeline, savebar), side),
    wiringBox,
  );

  function drawSave() {
    clear(savebar);
    savebar.hidden = !dirty;
    savebar.append(
      h("span", {}, "You have unsaved changes to your steps."),
      h("button", { class: "btn primary", "data-test": "save-workflow", onclick: () => guard(save) }, "Save workflow"),
      h("button", { class: "btn ghost", onclick: () => { steps = workflow.steps.map((s) => ({ ...s, settings: [...s.settings] })); dirty = false; drawSave(); draw(); } }, "Discard"));
  }
  async function save() {
    const saved = await api.put("/api/workflow", { version: 1, steps: steps.map(({ key, stepId, enabled, settings, label }) => ({ key, stepId, enabled, settings, label })) });
    workflow.steps = saved.steps;
    steps = saved.steps.map((s) => ({ ...s, settings: [...s.settings] }));
    dirty = false;
    drawSave(); draw();
    toast("Workflow saved. It applies from the next search.");
  }

  function stage(n, title, kind, ...body) {
    return h("section", { class: `stage ${kind}`, "data-test": `stage-${n}` },
      h("div", { class: "dot", "aria-hidden": "true" }, String(n)),
      h("div", { class: "card" }, h("header", {}, h("h3", {}, title), kind === "locked" ? h("span", { class: "badge good" }, "built in") : null), ...body));
  }

  function draw() {
    clear(pipeline);
    pipeline.append(
      stage(1, "Find postings", "locked",
        h("p", { class: "meta" }, `Every day at ${String(prefs.schedule.hour).padStart(2, "0")}:${String(prefs.schedule.minute).padStart(2, "0")} your time, from these employer boards and links.`),
        h("div", { class: "chips" }, [...prefs.boards, ...prefs.jobLinks].map((b) => h("span", { class: "badge" }, b.split("|").pop())), prefs.boards.length + prefs.jobLinks.length === 0 ? h("span", { class: "hint" }, "No boards yet.") : null),
        sourcesEditor()),
      stage(2, "Read the posting at the employer", "locked", h("p", { class: "meta" }, "Only the employer's own site counts as proof a listing is open.")),
      stage(3, "Screen against your résumé", "locked", h("p", { class: "meta" }, "Required skills, years, degree, clearance, pay and location, each with the posting's own words. Unknowns stay unknown.")),
      stage(4, "Your steps", "yours", yourSteps()),
      stage(5, "Decide (certified)", "locked",
        h("p", { class: "meta" }, "The certified recommendation rule decides strong fit, conditional fit, stretch or not eligible. A step of yours that says exclude stops drafting for that posting."),
        thresholds()),
      stage(6, "Draft materials", "locked", drafting()),
      stage(7, "Validate every claim (certified)", "locked", h("p", { class: "meta" }, "Every claim in a draft is checked against your résumé by the certified claims check, and every PDF is read back.")),
      stage(8, "Update your tracker", "locked",
        h("p", { class: "meta" }, "Your Google Sheet gets each job, its fit, and a “Your steps” column with what your steps said."),
        h("div", { class: "chips" }, steps.filter((s) => s.step?.kind === "column").map((s) => h("span", { class: "badge accent" }, s.label || s.step.name)))),
    );
  }

  function yourSteps() {
    const list = h("div", { class: "steps", "data-test": "your-steps" });
    if (steps.length === 0) list.append(h("div", { class: "empty" }, "No steps yet. Add one from the library, or describe what you want on the right."));
    let dragFrom = null;
    steps.forEach((s, i) => {
      const lib = s.step;
      const card = h("div", { class: `step${s.enabled ? "" : " off"}`, draggable: "true", "data-test": `step-${s.key}`, "data-key": s.key },
        h("div", { class: "row spread" },
          h("div", { class: "row" },
            h("span", { class: "handle", title: "Drag to reorder", "aria-hidden": "true" }, "⋮⋮"),
            h("strong", {}, s.label || lib?.name || s.stepId),
            lib ? h("span", { class: "badge" }, lib.kind === "check" ? "check" : "column") : null,
            lib?.visibility === "private" ? h("span", { class: "badge private" }, "private to you") : h("span", { class: "badge good" }, "certified · shared"),
            lib?.status === "quarantined" ? h("span", { class: "badge bad", title: lib.statusDetail || "" }, "switched off for everyone") : null),
          h("div", { class: "row" },
            h("label", { class: "check" }, h("input", { type: "checkbox", checked: s.enabled, "data-test": `step-toggle-${s.key}`, onchange: (e) => { s.enabled = e.target.checked; markDirty(); draw(); } }), "On"),
            h("button", { class: "btn small ghost", title: "Move up", disabled: i === 0, onclick: () => { steps.splice(i - 1, 0, steps.splice(i, 1)[0]); markDirty(); draw(); } }, "↑"),
            h("button", { class: "btn small ghost", title: "Move down", disabled: i === steps.length - 1, onclick: () => { steps.splice(i + 1, 0, steps.splice(i, 1)[0]); markDirty(); draw(); } }, "↓"),
            h("button", { class: "btn small danger", "data-test": `step-remove-${s.key}`, onclick: () => { steps.splice(i, 1); markDirty(); draw(); } }, "Remove"))),
        lib?.description ? h("p", { class: "meta" }, lib.description) : null,
        lib && lib.settings.length > 0 ? h("div", { class: "settings" }, lib.settings.map((setting, n) => h("div", {},
          h("label", { for: `set-${s.key}-${n}` }, setting.label),
          h("input", { type: "text", id: `set-${s.key}-${n}`, value: s.settings[n] || "", placeholder: setting.example, "data-test": `step-setting-${s.key}-${n}`,
            oninput: (e) => { s.settings[n] = e.target.value; markDirty(); } }),
          setting.help ? h("div", { class: "hint" }, setting.help) : null))) : null);
      card.addEventListener("dragstart", (e) => { dragFrom = i; card.classList.add("dragging"); e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", s.key); });
      card.addEventListener("dragend", () => card.classList.remove("dragging"));
      card.addEventListener("dragover", (e) => { e.preventDefault(); card.classList.add("drop-before"); });
      card.addEventListener("dragleave", () => card.classList.remove("drop-before"));
      card.addEventListener("drop", (e) => {
        e.preventDefault();
        card.classList.remove("drop-before");
        if (dragFrom === null || dragFrom === i) return;
        const [moved] = steps.splice(dragFrom, 1);
        steps.splice(dragFrom < i ? i - 1 : i, 0, moved);
        dragFrom = null; markDirty(); draw();
      });
      list.append(card);
    });
    return list;
  }

  function sourcesEditor() {
    const box = h("details", {}, h("summary", { "data-test": "edit-sources" }, "Edit what to search"));
    const roles = h("textarea", { "data-test": "roles" }, prefs.targetRoles.join("\n"));
    const boards = h("textarea", { "data-test": "boards" }, prefs.boards.join("\n"));
    const links = h("textarea", {}, prefs.jobLinks.join("\n"));
    const locations = h("textarea", {}, prefs.locations.join("\n"));
    const arr = ["remote", "hybrid", "onsite"].map((a) => h("input", { type: "checkbox", value: a, checked: prefs.workArrangements.includes(a) }));
    box.append(h("div", { class: "grid2" },
      field("Job titles you want (one per line)", roles),
      field("Employer boards (one per line, e.g. greenhouse:acme)", boards),
      field("Single posting links", links),
      h("div", {}, field("Locations", locations),
        h("div", { class: "row" }, arr.map((c) => h("label", { class: "check" }, c, c.value))))),
      h("button", { class: "btn", "data-test": "save-sources", onclick: () => guard(async () => {
        const lines = (t) => t.value.split("\n").map((x) => x.trim()).filter(Boolean);
        Object.assign(prefs, { targetRoles: lines(roles), boards: lines(boards), jobLinks: lines(links), locations: lines(locations), workArrangements: arr.filter((c) => c.checked).map((c) => c.value) });
        await api.put("/api/preferences", prefs);
        toast("Search settings saved."); draw();
      }) }, "Save search settings"));
    return box;
  }

  function thresholds() {
    const box = h("details", {}, h("summary", {}, "Adjust the thresholds"));
    const strong = h("input", { type: "number", min: 0, max: 100, value: Math.round(prefs.screening.strongThreshold * 100) });
    const conditional = h("input", { type: "number", min: 0, max: 100, value: Math.round(prefs.screening.conditionalThreshold * 100) });
    const short = h("input", { type: "number", min: 0, max: 20, step: 0.5, value: prefs.screening.maxYearsShort });
    const draftFor = ["strong_fit", "conditional_fit", "stretch"].map((r) => h("input", { type: "checkbox", value: r, checked: prefs.materials.draftFor.includes(r) }));
    box.append(h("div", { class: "grid2" },
      field("Skill coverage for a strong fit (%)", strong),
      field("Skill coverage for a conditional fit (%)", conditional),
      field("Years short that still count as conditional", short),
      h("div", {}, h("label", {}, "Draft materials for"), draftFor.map((c) => h("label", { class: "check" }, c, c.value.replace("_", " "))))),
      h("button", { class: "btn", onclick: () => guard(async () => {
        prefs.screening = { ...prefs.screening, strongThreshold: Number(strong.value) / 100, conditionalThreshold: Number(conditional.value) / 100, maxYearsShort: Number(short.value) };
        prefs.materials = { ...prefs.materials, draftFor: draftFor.filter((c) => c.checked).map((c) => c.value) };
        await api.put("/api/preferences", prefs); toast("Thresholds saved.");
      }) }, "Save thresholds"));
    return box;
  }

  function drafting() {
    const max = h("input", { type: "number", min: 0, max: 10, value: prefs.materials.maxNewDraftsPerRun });
    const wording = h("input", { type: "checkbox", checked: prefs.materials.modelCoverLetter, "data-test": "model-wording" });
    return h("div", {},
      h("p", { class: "meta" }, "A tailored résumé (your own bullets, reordered) and a cover letter with placeholders only you can fill, as Google Docs and PDFs."),
      h("div", { class: "grid2" },
        field("New drafts per search, at most", max),
        h("div", {}, h("label", { class: "check" }, wording, h("span", {}, "Let the private model on this server word my cover letters. ", h("span", { class: "hint" }, "Your résumé never leaves this server; every claim is still checked."))))),
      h("button", { class: "btn", onclick: () => guard(async () => {
        if (wording.checked && !state.me.consents["model-wording"]) await api.post("/api/me/consent", { kinds: ["model-wording"] });
        prefs.materials = { ...prefs.materials, maxNewDraftsPerRun: Number(max.value), modelCoverLetter: wording.checked };
        await api.put("/api/preferences", prefs);
        state.me = await api.get("/api/me");
        toast("Drafting settings saved.");
      }) }, "Save drafting"));
  }

  // side panel: library and the forge
  const search = h("input", { type: "search", placeholder: "Search the shared library", "aria-label": "Search the step library", "data-test": "library-search" });
  const results = h("div", { class: "lib-list", "data-test": "library-results" });
  const detail = h("div");
  async function findSteps() {
    const query = search.value.trim();
    const items = await api.get("/api/library" + (query ? "?q=" + encodeURIComponent(query) : ""));
    clear(results);
    if (!state.meta.libraryReady) {
      // A new server certifies its starter steps on first start; look again shortly.
      results.append(h("p", { class: "hint", "data-test": "library-warming" }, "The starter steps are still being certified on this server; more appear here in a minute or two."));
      clearTimeout(findSteps.retry);
      findSteps.retry = setTimeout(async () => {
        if (!results.isConnected) return;
        state.meta = await api.get("/api/meta");
        guard(findSteps);
      }, 5000);
    } else if (items.length === 0) results.append(h("p", { class: "hint" }, "No step matches. Describe it below and the studio will make one."));
    for (const item of items) {
      results.append(h("button", { class: "lib-item", "data-test": `library-item-${item.id}`, onclick: () => guard(() => showDetail(item.id)) },
        h("div", { class: "row spread" }, h("strong", {}, item.name), h("span", { class: "badge" }, item.kind)),
        h("div", { class: "hint" }, item.description),
        h("div", { class: "chips" },
          item.visibility === "private" ? h("span", { class: "badge private" }, "private") : h("span", { class: "badge good" }, "shared"),
          h("span", { class: "badge" }, `${item.uses} use${item.uses === 1 ? "" : "s"}`),
          item.status === "quarantined" ? h("span", { class: "badge bad" }, "switched off") : null)));
    }
  }
  async function showDetail(id) {
    const item = await api.get(`/api/library/${encodeURIComponent(id)}`);
    clear(detail).append(h("div", { class: "panel", "data-test": "library-detail" },
      h("h3", {}, item.name),
      h("p", {}, item.description),
      h("p", { class: "hint" }, item.behavior),
      h("div", { class: "chips" },
        h("span", { class: "badge good" }, `certified · ${item.mutants} mutants killed`),
        h("span", { class: "badge" }, item.writtenBy === "seed" ? "starter step" : item.writtenBy === "claude" ? "written by Claude from a generic design" : "written on this server"),
        h("span", { class: "badge", title: item.contentHash }, "content hash " + item.contentHash.slice(0, 10) + "…")),
      examplesTable(item),
      h("button", { class: "btn primary", "data-test": "add-step", disabled: item.status === "quarantined", onclick: () => {
        steps.push({ key: "s" + crypto.getRandomValues(new Uint32Array(1))[0].toString(16), stepId: item.id, enabled: true, settings: ["", "", ""], label: null, step: item });
        markDirty(); draw(); toast(`Added “${item.name}”. Fill in its settings, then save.`);
      } }, "Add to my pipeline")));
  }
  function examplesTable(item) {
    if (!item.examples?.length) return null;
    const show = ["title", "location", "arrangement", "payMax", "skillsMissing", "setting1", "setting2"].filter((k) => item.examples.some((e) => e.input[k]));
    const outs = Object.keys(item.examples[0].output);
    return h("details", {}, h("summary", {}, "What it does on made-up postings"),
      h("table", { class: "examples" },
        h("thead", {}, h("tr", {}, show.map((k) => h("th", {}, k)), outs.map((k) => h("th", {}, k)))),
        h("tbody", {}, item.examples.map((e) => h("tr", {}, show.map((k) => h("td", {}, (e.input[k] || "").slice(0, 60))), outs.map((k) => h("td", {}, e.output[k])))))));
  }
  search.addEventListener("input", () => { clearTimeout(search.t); search.t = setTimeout(() => guard(findSteps), 250); });

  const request = h("textarea", { placeholder: "e.g. Exclude anything that mentions on-call or weekend shifts", "aria-label": "Describe a new step", "data-test": "forge-request", maxlength: 2000 });
  const keepPrivate = h("input", { type: "checkbox", "data-test": "forge-private" });
  const progress = h("div", { "aria-live": "polite", "data-test": "forge-progress" });
  const ask = h("button", { class: "btn primary", "data-test": "forge-submit", onclick: () => guard(async () => {
    if (!request.value.trim()) return toast("Describe what the step should do.", true);
    ask.disabled = true;
    try {
      const { id } = await api.post("/api/forge", { request: request.value, private: keepPrivate.checked });
      await follow(id);
    } finally { ask.disabled = false; }
  }) }, "Make this step");
  async function follow(id) {
    for (;;) {
      const job = await api.get(`/api/forge/${id}`);
      clear(progress).append(h("div", { class: "panel" },
        h("div", { class: "row spread" }, h("strong", {}, "Your request"), h("span", { class: `badge ${job.status === "done" ? "good" : job.status === "failed" ? "bad" : "accent"}`, "data-test": "forge-status" }, job.status)),
        h("ol", { class: "log" }, job.log.map((l) => h("li", {}, l))),
        job.question ? h("p", {}, h("strong", {}, "One question: "), job.question) : null,
        job.error ? h("p", { class: "badge bad" }, job.error) : null));
      if (["done", "failed", "needs-input"].includes(job.status)) {
        if (job.status === "done") {
          const fresh = await api.get("/api/workflow");
          // Keep unsaved edits; take the new step the forge added.
          const known = new Set(steps.map((s) => s.key));
          for (const s of fresh.steps) if (!known.has(s.key)) steps.push({ ...s, settings: [...s.settings] });
          workflow.steps = fresh.steps;
          request.value = ""; draw(); await findSteps();
          toast(job.reused ? "Added a step from the library — no new code needed." : "Your new step is certified and in your pipeline.");
        }
        return;
      }
      await new Promise((r) => setTimeout(r, 1500));
    }
  }

  const wiringToggle = h("button", { class: "btn small", "data-test": "show-wiring", onclick: () => guard(async () => {
    if (wiringBox.firstChild) { clear(wiringBox); wiringToggle.textContent = "Show how it is wired"; return; }
    wiringToggle.textContent = "Hide wiring";
    wiringBox.append(wiring(await api.get("/api/workflow/graph")));
  }) }, "Show how it is wired");

  side.append(
    h("div", { class: "panel" },
      h("h2", {}, "Add a step"),
      search, results, detail),
    h("div", { class: "panel" },
      h("h2", {}, "Describe a new step"),
      h("p", { class: "hint" }, "Say what it should check or show. Your words are read only by the model on this server; if a new step is needed, Claude writes it from a generic description with no personal details, and Ashlar certifies it before it runs."),
      request,
      h("label", { class: "check" }, keepPrivate, h("span", {}, "Keep the new step private (others won't reuse it)")),
      h("div", { class: "row" }, ask, h("span", { class: "hint" }, `${forge.used} of ${forge.limit} new steps used this month; reusing a library step is free.`)),
      progress),
    h("div", { class: "panel" }, h("h2", {}, "Under the hood"), h("p", { class: "hint" }, "See the Ashlar workflow your pipeline compiles to: every node and every connection."), wiringToggle));

  draw(); drawSave(); await findSteps();
  const running = forge.jobs.find((j) => ["queued", "designing", "writing", "certifying"].includes(j.status));
  if (running) follow(running.id);
}

function wiring(graph) {
  // Layered layout: each node sits one column right of its furthest predecessor.
  const level = new Map(graph.nodes.map((n) => [n.id, 0]));
  for (let pass = 0; pass < graph.nodes.length; pass++)
    for (const e of graph.edges) level.set(e.to, Math.max(level.get(e.to), level.get(e.from) + 1));
  const brickNodes = graph.nodes.filter((n) => n.type === "brick");
  const cols = new Map();
  for (const n of brickNodes) { const l = level.get(n.id); if (!cols.has(l)) cols.set(l, []); cols.get(l).push(n); }
  const W = 220, H = 46, GX = 60, GY = 22;
  const pos = new Map();
  [...cols.keys()].sort((a, b) => a - b).forEach((l, ci) => cols.get(l).forEach((n, ri) => pos.set(n.id, { x: 20 + ci * (W + GX), y: 20 + ri * (H + GY) })));
  const width = 40 + cols.size * (W + GX), height = 40 + Math.max(...[...cols.values()].map((c) => c.length)) * (H + GY);
  const svg = h("svg", { svg: true, width, height, viewBox: `0 0 ${width} ${height}`, role: "img", "aria-label": "The compiled Ashlar workflow", "data-test": "wiring" });
  const seen = new Set();
  for (const e of graph.edges) {
    const a = pos.get(e.from), b = pos.get(e.to);
    if (!a || !b || seen.has(e.from + ">" + e.to)) continue;
    seen.add(e.from + ">" + e.to);
    const x1 = a.x + W, y1 = a.y + H / 2, x2 = b.x, y2 = b.y + H / 2;
    svg.append(h("path", { svg: true, d: `M${x1},${y1} C${x1 + 35},${y1} ${x2 - 35},${y2} ${x2},${y2}` }));
  }
  for (const n of brickNodes) {
    const p = pos.get(n.id);
    const cls = n.brickId?.startsWith("step.") ? "node user" : n.brickId?.startsWith("jobsearch.recommendation") || n.brickId === "jobsearch.validate-materials" ? "node certified" : "node";
    svg.append(h("g", { svg: true, class: cls },
      h("rect", { svg: true, x: p.x, y: p.y, width: W, height: H, rx: 8 }),
      h("text", { svg: true, x: p.x + 10, y: p.y + 19 }, (n.name || n.id).slice(0, 30)),
      h("text", { svg: true, x: p.x + 10, y: p.y + 35, opacity: 0.7 }, (n.brickId || "").slice(0, 32))));
  }
  return h("div", { class: "wiring" }, svg);
}

// ── tracker ────────────────────────────────────────────────────────────────────────────────────────────
async function tracker(main) {
  const jobs = await api.get("/api/tracker");
  main.append(h("h1", {}, "Tracker"),
    h("p", { class: "hint" }, "The same jobs as the Google Sheet in your Drive. Only you can mark a job applied, interviewing, offer or rejected, with a note on what happened."));
  if (jobs.length === 0) return main.append(h("div", { class: "empty" }, "No jobs yet. Run a search from Runs, or wait for your daily search."));
  main.append(h("table", { "data-test": "tracker" },
    h("thead", {}, h("tr", {}, ["Job", "Fit", "Your steps", "Status", "Notes"].map((t) => h("th", {}, t)))),
    h("tbody", {}, jobs.map((j) => {
      const status = h("select", { "aria-label": `Status of ${j.title}`, "data-test": `status-${j.id}` }, STATUSES.map((s) => h("option", { value: s, selected: s === j.status ? "selected" : null }, s.replace("_", " "))));
      status.value = j.status;
      // An assertion (applied, interviewing, offer, rejected) needs the user's own words about what happened.
      const evidenceBox = h("div", { hidden: true });
      const saveStatus = (evidence) => guard(async () => {
        await api.post(`/api/jobs/${encodeURIComponent(j.id)}/status`, { status: status.value, evidence });
        j.status = status.value; clear(evidenceBox).hidden = true; toast("Status saved.");
      });
      status.addEventListener("change", () => {
        if (!ASSERTIONS.has(status.value)) return saveStatus(null);
        const what = h("input", { type: "text", placeholder: "What happened? e.g. Applied on the employer's site today", "aria-label": "What happened", "data-test": `evidence-${j.id}` });
        clear(evidenceBox).hidden = false;
        evidenceBox.append(what, h("div", { class: "row" },
          h("button", { class: "btn small primary", "data-test": `evidence-save-${j.id}`, onclick: () => what.value.trim() ? saveStatus(what.value.trim()) : toast("Say what happened.", true) }, "Save"),
          h("button", { class: "btn small ghost", onclick: () => { status.value = j.status; clear(evidenceBox).hidden = true; } }, "Cancel")));
        what.focus();
      });
      return h("tr", {},
        h("td", {}, h("strong", {}, j.title), h("div", { class: "hint" }, `${j.employer}${j.location ? " · " + j.location : ""}${j.arrangement ? " · " + j.arrangement : ""}`),
          j.url ? h("a", { href: j.url, rel: "noopener noreferrer", target: "_blank" }, "posting") : null),
        h("td", {}, j.fit ? h("span", { class: `badge ${j.fit === "strong_fit" ? "good" : j.fit === "not_currently_eligible" ? "bad" : "warn"}` }, j.fit.replaceAll("_", " ")) : "—",
          j.excludedBy ? h("div", {}, h("span", { class: "badge bad" }, `excluded by “${j.excludedBy}”`)) : null,
          j.reason ? h("div", { class: "hint" }, j.reason) : null),
        h("td", {}, j.steps.map((s) => h("div", {}, h("span", { class: `badge ${s.verdict === "exclude" ? "bad" : s.verdict === "flag" ? "warn" : s.kind === "column" ? "accent" : "good"}` }, s.name), " ", s.text))),
        h("td", {}, status, evidenceBox),
        h("td", {}, h("div", { class: "hint" }, j.notes || ""), j.nextSteps ? h("div", {}, "Next: ", j.nextSteps) : null));
    }))));
}

// ── runs ───────────────────────────────────────────────────────────────────────────────────────────────
async function runs(main) {
  const list = await api.get("/api/runs");
  const go = h("button", { class: "btn primary", "data-test": "run-now", onclick: () => guard(async () => {
    go.disabled = true;
    await api.post("/api/runs");
    toast("Search started. It reads at most a few dozen postings, at human speed.");
    setTimeout(() => guard(route), 4000);
  }) }, "Run a search now");
  main.append(h("div", { class: "row spread" }, h("h1", {}, "Runs"), go));
  if (list.length === 0) return main.append(h("div", { class: "empty" }, "No searches yet."));
  main.append(h("table", { "data-test": "runs" },
    h("thead", {}, h("tr", {}, ["Started", "Status", "What happened"].map((t) => h("th", {}, t)))),
    h("tbody", {}, list.map((r) => h("tr", {},
      h("td", {}, new Date(r.startedAt).toLocaleString()),
      h("td", {}, h("span", { class: `badge ${r.status === "succeeded" ? "good" : r.status === "running" ? "accent" : "warn"}` }, r.status)),
      h("td", {}, r.summary ? `${r.summary.postingsRead ?? 0} postings read, ${r.summary.newJobs ?? 0} new, ${(r.summary.drafted || []).length} drafted` : r.error || ""))))));
}

// ── profile ────────────────────────────────────────────────────────────────────────────────────────────
async function profile(main) {
  const p = await api.get("/api/profile");
  const text = h("textarea", { placeholder: "Paste your résumé as plain text", "aria-label": "Your résumé as plain text", "data-test": "resume-text" });
  const file = h("input", { type: "file", accept: ".txt,.md,text/plain", "aria-label": "Upload a plain-text résumé" });
  file.addEventListener("change", async () => { if (file.files[0]) text.value = await file.files[0].text(); });
  main.append(h("h1", {}, "Profile"),
    h("div", { class: "panel" },
      h("h2", {}, p.hasResume ? "Replace your résumé" : "Add your résumé"),
      h("p", { class: "hint" }, "Plain text or Markdown. The studio derives a profile from it for you to confirm; nothing is invented and unknowns stay unknown."),
      text, h("div", { class: "row" }, file,
        h("button", { class: "btn primary", "data-test": "resume-save", onclick: () => guard(async () => {
          await api.post("/api/profile/resume", { filename: file.files[0]?.name || "resume.txt", text: text.value });
          toast("Résumé saved. Review the fields below."); await route();
        }) }, "Save résumé"))));
  if (!p.hasResume) return;
  main.append(h("div", { class: "panel" },
    h("h2", {}, "What the studio knows about you"),
    h("table", { "data-test": "profile-fields" },
      h("thead", {}, h("tr", {}, ["Field", "Value", "From", "Status", ""].map((t) => h("th", {}, t)))),
      h("tbody", {}, p.fields.filter((f) => !(f.name in USER_ONLY)).map((f) => h("tr", {},
        h("td", {}, f.name.replaceAll("_", " ")), h("td", { class: "mono" }, (f.value || "—").slice(0, 120)), h("td", { class: "hint" }, f.evidence || f.source || ""),
        h("td", {}, h("span", { class: `badge ${f.status === "confirmed" ? "good" : f.status === "rejected" ? "bad" : "warn"}` }, f.status)),
        h("td", {}, f.status !== "confirmed" && f.value ? h("button", { class: "btn small", onclick: () => guard(async () => { await api.post("/api/profile/review", { field: f.name, confirm: true }); await route(); }) }, "Confirm") : null,
          f.status !== "rejected" && f.value ? h("button", { class: "btn small ghost", onclick: () => guard(async () => { await api.post("/api/profile/review", { field: f.name, confirm: false }); await route(); }) }, "Reject") : null)))))),
    h("div", { class: "panel" },
      h("h2", {}, "Things only you can say"),
      h("p", { class: "hint" }, "The studio never guesses these. Leave any of them blank and postings that need them say so."),
      h("div", { class: "grid2" }, Object.entries(USER_ONLY).map(([name, label]) => {
        const current = p.fields.find((f) => f.name === name);
        const input = h("input", { type: "text", value: current?.value || "", "data-test": `field-${name}` });
        return h("div", {}, field(label, input),
          h("button", { class: "btn small", onclick: () => guard(async () => { await api.post("/api/profile/field", { field: name, value: input.value || null }); toast("Saved."); }) }, "Save"));
      }))));
}

// ── privacy & data ─────────────────────────────────────────────────────────────────────────────────────
async function privacy(main) {
  const confirm = h("input", { type: "text", placeholder: "Type DELETE", "data-test": "delete-confirm", "aria-label": "Type DELETE to confirm" });
  main.append(h("div", { class: "notice" },
    h("h1", {}, "Privacy & your data"),
    h("p", { class: "hint" }, `Privacy notice version ${state.meta.noticeVersion}. The operator of this server is the controller of your data; their contact details belong here.`),
    noticeSummary(),
    h("h2", {}, "What runs where"),
    h("table", {}, h("tbody", {},
      h("tr", {}, h("th", {}, "Your résumé, profile, preferences, tracker"), h("td", {}, "This server's database, and the files you asked for in your own Google Drive.")),
      h("tr", {}, h("th", {}, "Anything you type to describe a step"), h("td", {}, `Read only by the model on this server (${state.meta.privateModel}). It is discarded when the request finishes.`)),
      h("tr", {}, h("th", {}, "New step code"), h("td", {}, `Written by ${state.meta.stepWriter} from a generic description that passed a personal-data check, then certified by Ashlar. Shared steps contain no one's details.`)),
      h("tr", {}, h("th", {}, "Model-worded cover letters (optional)"), h("td", {}, "Only on this server's model, only if you turn it on.")),
      h("tr", {}, h("th", {}, "How long we keep things"), h("td", {}, "Archived jobs 365 days (unless you applied), run records 180 days. Your account until you delete it.")))),
    h("h2", {}, "Your consents"),
    h("ul", {}, Object.entries(state.me.consents).map(([k, v]) => h("li", {}, `${k.replaceAll("-", " ")}: ${v ? "given" : "not given"} `,
      v && k === "model-wording" ? h("button", { class: "btn small", onclick: () => guard(async () => { await api.post("/api/me/consent/withdraw", { kinds: [k] }); state.me = await api.get("/api/me"); await route(); }) }, "Withdraw") : null))),
    h("h2", {}, "Take your data with you"),
    h("p", {}, h("a", { class: "btn", href: "/api/me/export", download: "", "data-test": "export" }, "Download everything as JSON")),
    h("div", { class: "panel danger-zone" },
      h("h2", {}, "Delete your account"),
      h("p", {}, "This erases your account and everything the studio holds about you, now. Files the studio created in your Google Drive are yours and stay there. Steps you shared stay in the library with no link to you."),
      h("div", { class: "row" }, confirm, h("button", { class: "btn danger", "data-test": "delete-account", onclick: () => guard(async () => {
        await api.post("/api/me/delete", { confirm: confirm.value });
        state.me = null; location.hash = ""; toast("Your account has been deleted."); await boot();
      }) }, "Delete my account")))));
}

boot().catch((e) => { clear($("#main")).append(h("p", { class: "badge bad" }, "The studio could not start: " + e.message)); });
