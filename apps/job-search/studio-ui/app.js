// Job Search Studio UI. No dependencies, no third-party requests. Every write carries the X-Studio-Request header
// (the server refuses writes without it), and every piece of text from the server is set as text, never as HTML.

// ── vocabulary: what the server's enums are called on screen ───────────────────────────────────────────
const STATUS_LABEL = { not_applied: "Not applied", preparing: "Preparing", applied: "Applied", interviewing: "Interviewing", offer: "Offer", rejected: "Rejected", withdrawn: "Withdrawn", not_pursuing: "Not pursuing" };
const STATUS_GROUPS = [["Not yet", ["not_applied", "preparing"]], ["In progress", ["applied", "interviewing", "offer"]], ["Closed", ["rejected", "withdrawn", "not_pursuing"]]];
const ASSERTIONS = new Set(["applied", "interviewing", "offer", "rejected"]);
const FIT_LABEL = { strong_fit: ["Strong fit", "good"], conditional_fit: ["Conditional fit", "warn"], stretch: ["Stretch", "muted"], not_currently_eligible: ["Not currently eligible", "bad"] };
const REASON_LABEL = { "a strong match with questions for the user": "Strong on skills; a few things to check", "most requirements are met": "Most requirements met", "skills and experience meet the posting": "Skills and experience match the posting" };
const RUN_LABEL = { succeeded: ["Done", "good"], partial: ["Done, with gaps", "warn"], running: ["Running", "accent"], failed: ["Failed", "bad"], blocked_auth: ["Blocked: Google access", "bad"], skipped: ["Skipped", "muted"] };
const FIELD_LABEL = { name: "Name", email: "Email", phone: "Phone", location: "Location", years_experience: "Years of experience", skills: "Skills", employment: "Employment", education: "Education", certifications: "Certifications", clearance_claim: "Clearance (as the résumé says)" };
const USER_ONLY = [
  { name: "work_authorization", label: "Work authorization", type: "text", placeholder: "US citizen, permanent resident, needs sponsorship…" },
  { name: "requires_sponsorship", label: "Needs visa sponsorship", type: "yesno" },
  { name: "clearance_status", label: "Clearance status", type: "text", placeholder: "active Secret, none…", hint: "Optional. Used only to flag postings that require one." },
  { name: "willing_to_relocate", label: "Willing to relocate", type: "yesno" },
  { name: "max_travel_pct", label: "Most travel you accept (%)", type: "number" },
  { name: "earliest_start_date", label: "Earliest start date", type: "date" },
];
const FORGE_STEPS = [["Reading your request", "Read your request", "by the model on this server"], ["Writing the step", "Wrote the step", "Claude, an AI service outside this server, from a description with no personal data"], ["Checking it", "Checked", "Ashlar runs it against test cases, plants bugs to prove the tests catch them, and confirms it cannot reach the internet or your files"], ["Added to your pipeline", "Added to your pipeline", ""]];
const FORGE_INDEX = { queued: 0, designing: 0, "needs-input": 0, writing: 1, certifying: 2, done: 3 };
const ACTIVE_FORGE = new Set(["queued", "designing", "writing", "certifying"]);

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
  return h("div", { class: "field" }, h("label", { for: control.id }, text), control, hint ? h("div", { class: "hint" }, hint) : null);
}
function clear(el) { while (el.firstChild) el.firstChild.remove(); return el; }
/** append() that drops null/false like h() does, so a conditional child never becomes the text "null". */
function add(el, ...children) {
  for (const c of children.flat(Infinity)) if (c !== null && c !== undefined && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}
function vh(text) { return h("span", { class: "vh" }, text); }

// One 16px grid, 1.5px strokes, round caps; tinted only by currentColor.
const ICONS = {
  "chevron-right": [["path", { d: "M6 3.5 10.5 8 6 12.5" }]],
  "chevron-down": [["path", { d: "M3.5 6 8 10.5 12.5 6" }]],
  "arrow-left": [["path", { d: "M13 8H3M7.5 3.5 3 8l4.5 4.5" }]],
  "arrow-up": [["path", { d: "M8 13V3M3.5 7.5 8 3l4.5 4.5" }]],
  "arrow-down": [["path", { d: "M8 3v10M3.5 8.5 8 13l4.5-4.5" }]],
  grip: [["circle", { cx: 6, cy: 4, r: 1 }], ["circle", { cx: 10, cy: 4, r: 1 }], ["circle", { cx: 6, cy: 8, r: 1 }], ["circle", { cx: 10, cy: 8, r: 1 }], ["circle", { cx: 6, cy: 12, r: 1 }], ["circle", { cx: 10, cy: 12, r: 1 }]],
  external: [["path", { d: "M9 3h4v4M13 3 7 9M11 9.5V12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h2.5" }]],
  lock: [["rect", { x: 3.5, y: 7, width: 9, height: 6.5, rx: 1.2 }], ["path", { d: "M5.5 7V5a2.5 2.5 0 0 1 5 0v2" }]],
  check: [["path", { d: "M3.5 8.5l3 3 6-7" }]],
  plus: [["path", { d: "M8 3v10M3 8h10" }]],
  close: [["path", { d: "M4 4l8 8M12 4l-8 8" }]],
  server: [["rect", { x: 2.5, y: 3, width: 11, height: 4, rx: 1 }], ["rect", { x: 2.5, y: 9, width: 11, height: 4, rx: 1 }], ["path", { d: "M5 5h.01M5 11h.01" }]],
  drive: [["path", { d: "M2.5 5.5A1.5 1.5 0 0 1 4 4h3l1.5 1.5H12a1.5 1.5 0 0 1 1.5 1.5v4.5A1.5 1.5 0 0 1 12 13H4a1.5 1.5 0 0 1-1.5-1.5z" }]],
  shield: [["path", { d: "M8 2.5l4.5 1.7v3.6c0 2.8-1.9 4.9-4.5 5.7C5.4 12.7 3.5 10.6 3.5 7.8V4.2z" }]],
  "check-circle": [["circle", { cx: 8, cy: 8, r: 5.5 }], ["path", { d: "M5.5 8l1.8 1.8L10.8 6" }]],
  bars: [["path", { d: "M3 4h10M3 8h7M3 12h4" }]],
  exit: [["path", { d: "M6 3H4a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h2M10.5 11l3-3-3-3M13.5 8H6.5" }]],
};
function icon(name, size = 16) {
  const svg = h("svg", { svg: true, viewBox: "0 0 16 16", width: size, height: size, fill: "none", stroke: "currentColor", "stroke-width": 1.5, "stroke-linecap": "round", "stroke-linejoin": "round", "aria-hidden": "true" });
  for (const [tag, attrs] of ICONS[name]) svg.append(h(tag, { svg: true, ...attrs }));
  if (name === "grip") { svg.setAttribute("fill", "currentColor"); svg.setAttribute("stroke", "none"); }
  return svg;
}

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

/** Says something to assistive tech without showing it: a cleared-then-filled live region, so it is read every time. */
function announce(text) { const a = $("#announce"); a.textContent = ""; setTimeout(() => (a.textContent = text), 50); }
/** The one toast. Errors stay until dismissed; an action (e.g. Undo) sits beside the message. */
function toast(message, opts = {}) {
  const o = typeof opts === "boolean" ? { error: opts } : opts;
  const t = clear($("#toast"));
  t.className = "toast" + (o.error ? " error" : "");
  add(t, h("span", { class: "msg" }, message),
    o.action ? h("button", { class: "btn ghost small", onclick: () => { t.hidden = true; o.action.onclick(); } }, o.action.label) : null,
    h("button", { class: "btn icon", "aria-label": "Dismiss", onclick: () => { t.hidden = true; $("#main").focus({ preventScroll: true }); } }, icon("close")));
  t.hidden = false;
  announce(message);
  clearTimeout(toast.timer);
  if (!o.error) toast.timer = setTimeout(() => (t.hidden = true), o.duration ?? 3500);
}
async function guard(work) {
  try { return await work(); }
  catch (e) {
    if (e.status === 401) { await boot(); return; }
    if (e.message === "consent-required") { location.hash = "#/welcome"; await boot(); return; }
    // Shown once, here; not rethrown, so a handled error never becomes an uncaught one.
    toast(e.message || "Something went wrong", { error: true });
    return undefined;
  }
}
/** Runs a save from a button: aria-busy while in flight, then a short "Saved" beside it instead of a toast. */
async function saving(button, work, done = "Saved") {
  const label = button.textContent;
  button.setAttribute("aria-busy", "true"); button.disabled = true; button.textContent = "Saving…";
  try {
    await work();
    let note = button.nextElementSibling;
    if (!note || !note.classList.contains("saved")) { note = h("span", { class: "saved", role: "status" }); button.after(note); }
    note.textContent = done;
    clearTimeout(note.timer); note.timer = setTimeout(() => (note.textContent = ""), 2000);
  } finally { button.removeAttribute("aria-busy"); button.disabled = false; button.textContent = label; }
}
const reducedMotion = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
function reveal(el, block = "center") { el.scrollIntoView({ behavior: reducedMotion() ? "auto" : "smooth", block }); }

// ── app state and routing ──────────────────────────────────────────────────────────────────────────────
const state = { meta: null, me: null };
const drafts = {};
let viewAbort = null;

async function boot() {
  state.meta = await api.get("/api/meta");
  $("#synthetic-banner").hidden = !state.meta.synthetic;
  $("#build").textContent = state.meta.build && state.meta.build !== "dev" ? `build ${state.meta.build}` : "";
  try { state.me = await api.get("/api/me"); }
  catch (e) { if (e.status !== 401) throw e; state.me = null; }
  renderWho();
  await route();
}

function renderWho() {
  const who = clear($("#who"));
  const signedIn = !!(state.me && state.me.onboarded);
  $("#nav").hidden = !signedIn;
  document.body.classList.toggle("signed-in", signedIn);
  if (!state.me) return;
  who.append(
    h("span", { "data-test": "signed-in-as", title: state.me.displayName }, state.me.displayName),
    h("button", { class: "btn ghost small", "data-test": "sign-out", "aria-label": "Sign out", onclick: () => guard(async () => {
      await api.post("/auth/signout"); forgetDrafts(); state.me = null; location.hash = ""; await boot();
    }) }, icon("exit"), h("span", { class: "label" }, "Sign out")),
  );
}

function backLink(href, text, test) {
  return h("p", {}, h("a", { class: "btn ghost small back", href, "data-test": test }, icon("arrow-left"), text));
}

async function route() {
  viewAbort?.abort();
  viewAbort = new AbortController();
  const t = $("#toast");
  if (t.classList.contains("error")) t.hidden = true;
  const main = clear($("#main"));
  const [path0, query] = (location.hash.replace(/^#/, "") || "/designer").split("?");
  const path = path0 || "/designer";
  if (!state.me) {
    main.className = "reading";
    if (path === "/privacy") { main.append(backLink("#/", "Back to sign in", "back-to-sign-in")); return privacy(main); }
    return signIn(main);
  }
  if (!state.me.onboarded) {
    main.className = "reading";
    // The notice must be readable before anyone agrees to it.
    if (path === "/privacy") { main.append(backLink("#/welcome", "Back", "back-to-welcome")); return privacy(main); }
    return welcome(main);
  }
  for (const a of document.querySelectorAll("#nav a")) a.setAttribute("aria-current", a.getAttribute("href") === "#" + path ? "page" : "false");
  const views = { "/designer": designer, "/tracker": tracker, "/runs": runs, "/profile": profile, "/privacy": privacy, "/welcome": designer };
  const view = views[path] || designer;
  main.className = view === privacy ? "reading" : "working";
  const sk = h("div", { class: "skeleton", "aria-hidden": "true" }, h("span"), h("span"), h("span"));
  main.append(sk);
  try { await view(main, new URLSearchParams(query || ""), viewAbort.signal); }
  catch (e) {
    if (e.status === 401) { await boot(); return; }
    clear(main).append(h("div", { class: "empty" }, h("strong", {}, "Couldn't load this page."), h("p", {}, e.message || String(e)),
      h("div", { class: "actions" }, h("button", { class: "btn", onclick: () => guard(route) }, "Try again"))));
  }
  sk.remove();
  main.focus({ preventScroll: true });
}
window.addEventListener("hashchange", () => guard(route));
// "Skip to content" moves focus; it must not change the hash, which would re-route to the designer.
$(".skip").addEventListener("click", (e) => { e.preventDefault(); const m = $("#main"); m.focus({ preventScroll: true }); reveal(m, "start"); });
// Sticky table heads sit under the header + banner, whatever height they take.
new ResizeObserver(([e]) => document.documentElement.style.setProperty("--top-h", `${Math.round(e.contentRect.height)}px`)).observe($(".top"));

// Unsaved step changes survive a wander to another page, per account; cleared on save, sign-out and deletion.
function draftKey() { return `studio-draft:${state.me.id}`; }
function loadDraft() {
  if (drafts[state.me.id]) return drafts[state.me.id];
  try { const s = sessionStorage.getItem(draftKey()); return s ? JSON.parse(s) : null; } catch { return null; }
}
function saveDraft(d) {
  if (d) drafts[state.me.id] = d; else delete drafts[state.me.id];
  try { d ? sessionStorage.setItem(draftKey(), JSON.stringify(d)) : sessionStorage.removeItem(draftKey()); } catch { /* storage may be off */ }
}
function forgetDrafts() {
  for (const k of Object.keys(drafts)) delete drafts[k];
  try { for (const k of Object.keys(sessionStorage)) if (k.startsWith("studio-draft:")) sessionStorage.removeItem(k); } catch { /* storage may be off */ }
}

// ── trust strip: the three facts, said once ────────────────────────────────────────────────────────────
function trustStrip(large) {
  const row = (ic, head, body) => [h("dt", {}, icon(ic), h("span", {}, head)), h("dd", {}, body)];
  return h("dl", { class: `trust${large ? " large" : ""}` },
    row("server", "Stays on this server.", "Your résumé, profile and anything you type are read only by a model running here."),
    row("drive", "Lives in your Drive.", "Drafts, PDFs and the tracker are files in your own Google Drive."),
    row("shield", "Nothing about you leaves this server.", "When you ask for a new step, only a plain description of the rule, with nothing about you in it, goes to Claude, an AI service outside this server, to write the code. Ashlar, the studio's test gate, checks that code before it can run, and it runs with no access to the internet or your files."));
}
function initials(name) {
  const parts = String(name || "").trim().split(/\s+/);
  return h("span", { class: "initials", "aria-hidden": "true" }, (parts[0]?.[0] || "") + (parts[1]?.[0] || ""));
}
function googleMark() {
  const svg = h("svg", { svg: true, viewBox: "0 0 48 48", width: 18, height: 18, "aria-hidden": "true" });
  svg.append(
    h("path", { svg: true, fill: "#EA4335", d: "M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z" }),
    h("path", { svg: true, fill: "#4285F4", d: "M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z" }),
    h("path", { svg: true, fill: "#FBBC05", d: "M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z" }),
    h("path", { svg: true, fill: "#34A853", d: "M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z" }));
  return svg;
}

// ── sign-in and first run ──────────────────────────────────────────────────────────────────────────────
function signIn(main) {
  const failed = new URLSearchParams(location.search).get("signin");
  const action = h("div", { class: "action" });
  if (failed) action.append(h("p", { class: "error-line" }, "Sign-in did not complete. Please try again."));
  if (state.meta.googleSignIn)
    action.append(
      h("a", { class: "btn google", href: "/auth/google/start", "data-test": "google-sign-in" }, googleMark(), "Sign in with Google"),
      h("p", { class: "hint" }, "Google tells the studio your name and email and lets it write in your Drive. The studio never shares your Drive or applies for you."));
  if (state.meta.synthetic)
    action.append(
      h("h2", { class: "h-small" }, "Try it as a made-up person"),
      h("p", { class: "hint" }, "This server is a synthetic copy. These people and their résumés are invented."),
      h("div", { class: "personas" }, (state.meta.personas || []).map((p) =>
        h("button", { class: "btn", "data-test": `persona-${p.email}`, onclick: () => guard(async () => { await api.post("/auth/dev", { email: p.email }); await boot(); }) }, initials(p.displayName), p.displayName))));
  if (!state.meta.googleSignIn && !state.meta.synthetic) action.append(h("p", { class: "meaning" }, "Sign-in is not configured on this server yet."));
  main.append(h("section", { class: "hero" },
    h("h1", {}, "Your job search, designed by you."),
    h("p", { class: "lede" }, "Build the pipeline that reads postings for you. Your documents stay in your own Google Drive."),
    action,
    h("hr", { class: "hr" }),
    trustStrip(false),
    h("p", { class: "hint mt4" }, "Nothing is sent for you. The studio never applies or messages an employer without your say-so."),
    h("details", {}, h("summary", {}, "How this works"),
      h("ul", { class: "list" },
        h("li", {}, "Every day the studio reads the employer boards you list, screens each posting against your résumé, and decides how well it fits. Unknowns stay unknown."),
        h("li", {}, "You can add steps from a shared library, or describe one in plain words. Every step is tested by Ashlar before it runs, and it runs with no access to the internet or your files."),
        h("li", {}, "Qualifying jobs get a tailored résumé and a cover letter as Google Docs and PDFs, every claim checked against your résumé; you review and send them yourself."))),
    h("p", { class: "hint mt5" }, h("a", { href: "#/privacy" }, "Privacy notice")),
  ));
}

function welcome(main) {
  const boxes = {
    "privacy-notice": h("input", { type: "checkbox", id: "c-notice", "data-test": "consent-privacy-notice" }),
    terms: h("input", { type: "checkbox", id: "c-terms", "data-test": "consent-terms" }),
    "age-16-or-over": h("input", { type: "checkbox", id: "c-age", "data-test": "consent-age" }),
  };
  const go = h("button", { class: "btn primary", "data-test": "consent-continue", disabled: true }, "Continue to your profile");
  const update = () => (go.disabled = !Object.values(boxes).every((b) => b.checked));
  Object.values(boxes).forEach((b) => b.addEventListener("change", update));
  go.addEventListener("click", () => guard(async () => {
    await api.post("/api/me/consent", { kinds: Object.keys(boxes), timezone: Intl.DateTimeFormat().resolvedOptions().timeZone });
    location.hash = "#/profile";
    await boot();
  }));
  main.append(h("section", {},
    h("h1", {}, `Welcome, ${state.me.displayName}`),
    h("p", { class: "lede" }, "Before the studio stores or reads anything about your search, here is how it handles your data."),
    h("div", { class: "mt5" }, trustStrip(true)),
    h("p", { class: "meaning mt4" }, "Full notice: ", h("a", { href: "#/privacy", "data-test": "read-notice" }, "Privacy & your data")),
    h("h2", { class: "mt7" }, "Before you start"),
    h("div", { class: "consents" },
      h("label", { class: "check", for: "c-notice" }, boxes["privacy-notice"], h("span", {}, `I have read the privacy notice (version ${state.meta.noticeVersion}).`)),
      h("label", { class: "check", for: "c-terms" }, boxes.terms, h("span", {}, "I agree to the terms of use.")),
      h("label", { class: "check", for: "c-age" }, boxes["age-16-or-over"], h("span", {}, "I am 16 or older."))),
    h("div", { class: "actions" }, go),
    h("p", { class: "hint" }, "All three are needed to use the studio. Nothing about your search is stored until you continue."),
  ));
}

// ── designer ───────────────────────────────────────────────────────────────────────────────────────────
async function designer(main, _query, signal) {
  const [workflow, prefs, forge] = await Promise.all([api.get("/api/workflow"), api.get("/api/preferences"), api.get("/api/forge")]);
  const clone = (list) => list.map((s) => ({ ...s, settings: [...s.settings] }));
  const draft = loadDraft();
  let steps, dirty = false;
  if (draft?.dirty) {
    // A draft from earlier in this session: keep it, refresh library details, and take in steps the forge added meanwhile.
    const known = new Set(draft.baseKeys || []);
    const byKey = new Map(workflow.steps.map((s) => [s.key, s]));
    steps = draft.steps.map((s) => ({ ...s, step: byKey.get(s.key)?.step ?? s.step }));
    for (const s of workflow.steps) if (!known.has(s.key) && !steps.some((d) => d.key === s.key)) steps.push({ ...s, settings: [...s.settings] });
    dirty = true;
  } else steps = clone(workflow.steps);
  let insertAt = null;
  let lastRequest = draft?.lastRequest || "";
  const persist = () => saveDraft(dirty || lastRequest ? { dirty, steps, baseKeys: workflow.steps.map((s) => s.key), lastRequest } : null);
  const markDirty = () => { dirty = true; drawSave(); persist(); };

  const pipeline = h("div", { class: "pipeline", "data-test": "pipeline" });
  const savebar = h("div", { class: "savebar", hidden: true });
  const side = h("aside", { class: "side" });
  const wiringBox = h("div", { id: "wiring-box" });
  const grid = h("div", { class: "designer" }, h("div", {}, pipeline));
  main.append(
    h("div", { class: "page-head indent" }, h("div", {}, h("h1", {}, "Your pipeline"), h("p", { class: "lede" }, "Every posting found for you runs through these steps, top to bottom."))),
    grid);

  // ── the save bar ──
  function drawSave() {
    clear(savebar);
    savebar.hidden = !dirty;
    document.body.classList.toggle("has-savebar", dirty);
    if (!dirty) return;
    savebar.append(
      h("span", { class: "dot small accent", "aria-hidden": "true" }),
      h("span", { class: "msg" }, "Unsaved step changes"),
      h("span", { class: "grow" }),
      h("button", { class: "btn ghost", onclick: discard }, "Discard"),
      h("button", { class: "btn primary", "data-test": "save-workflow", onclick: () => guard(save) }, "Save"));
  }
  async function save() {
    const saved = await api.put("/api/workflow", { version: 1, steps: steps.map(({ key, stepId, enabled, settings, label }) => ({ key, stepId, enabled, settings, label })) });
    workflow.steps = saved.steps;
    steps = clone(saved.steps);
    dirty = false; saveDraft(lastRequest ? { dirty: false, steps: [], baseKeys: [], lastRequest } : null);
    drawSave(); redrawSteps();
    (stepsList.firstElementChild?.querySelector(".switch") || addRow).focus({ preventScroll: true });
    toast("Pipeline saved. It applies from the next search.");
  }
  function discard() {
    const kept = steps;
    steps = clone(workflow.steps); dirty = false; persist(); drawSave(); redrawSteps();
    toast("Step changes discarded", { duration: 7000, action: { label: "Undo", onclick: () => { steps = kept; markDirty(); redrawSteps(); } } });
  }

  // ── the eight stages ──
  function stage(n, kind, title, meaning, ...body) {
    return h("section", { class: `stage ${kind}`, "data-test": `stage-${n}` },
      h("div", { class: "numeral", "aria-hidden": "true" }, String(n)),
      h("div", { class: "stage-body" },
        h("div", { class: "stage-title" }, h("h2", {}, title), kind === "always" ? h("span", { class: "tag", title: "Always on" }, icon("lock"), vh("Always on")) : null),
        meaning ? h("p", { class: "meaning" }, meaning) : null,
        ...body));
  }
  const hhmm = () => `${String(prefs.schedule.hour).padStart(2, "0")}:${String(prefs.schedule.minute).padStart(2, "0")}`;
  const sourcesSummary = h("p", { class: "summary-line" });
  const decideSummary = h("p", { class: "summary-line" });
  const draftSummary = h("p", { class: "summary-line" });
  const columnsLine = h("p", { class: "caption mt1" });
  function refreshSummaries() {
    const sources = [...prefs.boards, ...prefs.jobLinks].map((b) => b.split("|").pop());
    add(clear(sourcesSummary), `Every day at ${hhmm()} your time`, sources.length ? ` · ${sources.join(" · ")}` : null, sources.length ? null : h("span", { class: "hint" }, " · No boards yet"));
    decideSummary.textContent = `Strong fit when you have ${Math.round(prefs.screening.strongThreshold * 100)}% of the required skills · conditional from ${Math.round(prefs.screening.conditionalThreshold * 100)}% · up to ${prefs.screening.maxYearsShort} years under the required experience still counts as conditional`;
    draftSummary.textContent = `Up to ${prefs.materials.maxNewDraftsPerRun} drafts per search · cover letters ${prefs.materials.modelCoverLetter ? "worded by the model on this server" : "from a template, in your own words"}`;
    const cols = steps.filter((s) => s.step?.kind === "column").map((s) => s.label || s.step.name);
    columnsLine.textContent = cols.length ? `Columns from your steps: ${cols.join(" · ")}` : "";
  }

  const stepsList = h("div", { class: "steps", "data-test": "your-steps" });
  const stepsCount = h("span", { class: "caption" });
  const paletteSlot = h("div", { class: "palette-slot" });
  const addRow = h("button", { class: "add-row", type: "button", onclick: () => {
    insertAt = steps.length;
    if (wide.matches) search.focus();
    else { reveal(side, "start"); search.focus({ preventScroll: true }); }
  } }, icon("plus"), "Add a step");

  pipeline.append(
    stage(1, "locked", "Find postings", "From your employer boards and single links.", sourcesSummary, sourcesEditor()),
    stage(2, "always", "Read the posting at the employer", "Only the employer's own site counts as proof a listing is open."),
    stage(3, "locked", "Screen against your résumé", "Required skills, years, degree, clearance, pay and location, in the posting's own words. Unknowns stay unknown."),
    h("section", { class: "stage yours", "data-test": "stage-4" },
      h("div", { class: "numeral", "aria-hidden": "true" }, "4"),
      h("div", { class: "stage-body" },
        h("div", { class: "stage-title" }, h("h2", {}, "Your steps"), stepsCount),
        stepsList, addRow, savebar, paletteSlot)),
    stage(5, "locked", "Decide", "Turns the screen into a verdict: strong fit, conditional fit, stretch, or not eligible. If one of your steps says exclude, nothing is drafted.", decideSummary, thresholds()),
    stage(6, "locked", "Draft materials", "A tailored résumé (your own bullets, reordered) and a cover letter with placeholders only you can fill, as Google Docs and PDFs.", draftSummary, drafting()),
    stage(7, "always", "Validate every claim", "Every line in a draft is checked against your résumé before it is kept, and every PDF is opened and re-read to make sure it matches the Doc."),
    stage(8, "locked", "Update your tracker", "Your Google Sheet gets each job, its fit and a “Your steps” column.", columnsLine),
  );
  const wiringToggle = h("button", { class: "btn ghost small", "data-test": "show-wiring", "aria-expanded": "false", "aria-controls": "wiring-box", onclick: () => guard(async () => {
    if (wiringBox.firstChild) { clear(wiringBox); wiringToggle.textContent = "Show how it is wired"; wiringToggle.setAttribute("aria-expanded", "false"); return; }
    wiringToggle.textContent = "Hide wiring"; wiringToggle.setAttribute("aria-expanded", "true");
    wiringBox.append(wiring(await api.get("/api/workflow/graph")));
    reveal(wiringBox, "start");
  }) }, "Show how it is wired");
  grid.firstChild.append(
    h("div", { class: "pipeline-foot" },
      h("p", { class: "caption" }, `Your words: ${forge.privateModel} on this server · Step code: ${forge.writer} · Certification: Ashlar`),
      wiringToggle));
  // The wiring spans the whole content width below both columns: six brick columns need the room.
  grid.firstChild.append(wiringBox);

  // ── your steps ──
  function redrawSteps(focus) {
    clear(stepsList);
    refreshSummaries();
    stepsCount.textContent = `${steps.length} ${steps.length === 1 ? "step" : "steps"} · run between screening and the decision${steps.length > 1 ? " · reorder with the arrows or Alt+↑/↓" : ""}`;
    if (steps.length === 0) {
      stepsList.append(h("div", { class: "empty" }, h("strong", {}, "No steps of your own yet."), h("p", {}, "Postings go straight from screening to the decision. Add a step to rule some out, flag them, or add a column to your tracker.")));
      return;
    }
    let dragFrom = null;
    steps.forEach((s, i) => {
      const lib = s.step;
      const name = s.label || lib?.name || s.stepId;
      const move = (from, to) => {
        const key = steps[from].key, which = document.activeElement?.classList.contains("down") ? "down" : "up";
        steps.splice(to, 0, steps.splice(from, 1)[0]); markDirty();
        redrawSteps(`[data-key="${key}"] .move.${which}:not(:disabled), [data-key="${key}"] .move:not(:disabled), [data-key="${key}"] .switch`);
      };
      const toggle = h("input", { type: "checkbox", class: "switch", checked: s.enabled, disabled: !lib, "data-test": `step-toggle-${s.key}`, onchange: (e) => { s.enabled = e.target.checked; card.classList.toggle("off", !s.enabled); markDirty(); } });
      const remove = h("button", { class: "btn icon", "data-test": `step-remove-${s.key}`, "aria-label": `Remove “${name}”` }, icon("close"));
      remove.addEventListener("click", () => {
        const hasValues = s.settings.some((v) => v && v.trim());
        if (hasValues && !remove.dataset.armed) {
          remove.dataset.armed = "1"; remove.replaceChildren("Remove?"); remove.className = "btn small danger"; remove.setAttribute("aria-label", `Remove “${name}” and its settings`);
          setTimeout(() => { if (remove.isConnected) { delete remove.dataset.armed; remove.className = "btn icon"; remove.replaceChildren(icon("close")); remove.setAttribute("aria-label", `Remove “${name}”`); } }, 3000);
          return;
        }
        steps.splice(i, 1); markDirty(); redrawSteps();
        (stepsList.querySelector(`.step:nth-child(${i + 1}) [data-test^="step-remove-"]`) || addRow).focus({ preventScroll: true });
      });
      const card = h("div", { class: `card step${s.enabled ? "" : " off"}`, draggable: "true", "data-test": `step-${s.key}`, "data-key": s.key },
        h("div", { class: "step-head" },
          h("span", { class: "grip", title: "Drag to reorder", "aria-hidden": "true" }, icon("grip")),
          lib ? h("span", { class: "kind" }, icon(lib.kind === "check" ? "check-circle" : "bars"), vh(lib.kind === "check" ? "check: " : "column: ")) : null,
          h("span", { class: "name" }, name),
          h("div", { class: "cluster" },
            h("label", { class: "switch-label" }, toggle, vh(`On: ${name}`)),
            h("button", { class: "btn icon move up", title: "Move up", "aria-label": `Move “${name}” up`, "aria-keyshortcuts": "Alt+ArrowUp", disabled: i === 0, onclick: () => move(i, i - 1) }, icon("arrow-up")),
            h("button", { class: "btn icon move down", title: "Move down", "aria-label": `Move “${name}” down`, "aria-keyshortcuts": "Alt+ArrowDown", disabled: i === steps.length - 1, onclick: () => move(i, i + 1) }, icon("arrow-down")),
            remove)),
        !lib ? h("div", { class: "tags" }, h("span", { class: "tag bad" }, "No longer in the library")) : null,
        lib && (lib.visibility === "private" || lib.status === "quarantined") ? h("div", { class: "tags" },
          lib.visibility === "private" ? h("span", { class: "tag" }, icon("lock"), "private to you") : null,
          lib.status === "quarantined" ? h("span", { class: "tag bad", title: lib.statusDetail || "" }, "switched off for everyone") : null) : null,
        lib?.description ? h("p", { class: "desc" }, lib.description) : null,
        lib && lib.settings.length > 0 ? h("div", { class: "settings" }, lib.settings.map((setting, n) => h("div", { class: "field" },
          h("label", { for: `set-${s.key}-${n}` }, setting.label),
          h("input", { type: "text", id: `set-${s.key}-${n}`, value: s.settings[n] || "", placeholder: setting.example, "data-test": `step-setting-${s.key}-${n}`,
            oninput: (e) => { s.settings[n] = e.target.value; markDirty(); } }),
          setting.help ? h("div", { class: "hint" }, setting.help) : null))) : null,
        i < steps.length - 1 ? h("button", { class: "seam", type: "button", "aria-label": "Add a step here", onclick: () => { insertAt = i + 1; search.focus(); } }, h("span", {}, icon("plus"))) : null);
      card.addEventListener("keydown", (e) => {
        if (!e.altKey || (e.key !== "ArrowUp" && e.key !== "ArrowDown")) return;
        e.preventDefault();
        if (e.key === "ArrowUp" && i > 0) move(i, i - 1);
        if (e.key === "ArrowDown" && i < steps.length - 1) move(i, i + 1);
      });
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
        dragFrom = null; markDirty(); redrawSteps();
      });
      stepsList.append(card);
    });
    if (focus) stepsList.querySelector(focus)?.focus({ preventScroll: true });
  }
  function addStep(item) {
    const key = "s" + crypto.getRandomValues(new Uint32Array(1))[0].toString(16);
    const at = insertAt ?? steps.length;
    steps.splice(at, 0, { key, stepId: item.id, enabled: true, settings: ["", "", ""], label: null, step: item });
    insertAt = null;
    markDirty(); redrawSteps();
    highlight(key);
    if (wide.matches) stepsList.querySelector(`[data-key="${key}"] input[type="text"], [data-key="${key}"] .switch`)?.focus({ preventScroll: true });
  }
  function highlight(key) {
    const el = stepsList.querySelector(`[data-key="${key}"]`);
    if (!el) return false;
    el.classList.add("new"); reveal(el);
    requestAnimationFrame(() => requestAnimationFrame(() => el.classList.remove("new")));
    return true;
  }

  // ── the built-in stages' settings ──
  function editor(summaryText, summaryAttrs, body, saveLabel, saveAttrs, onSave) {
    const saveBtn = h("button", { class: "btn small", ...saveAttrs, "aria-label": saveLabel }, "Save");
    saveBtn.addEventListener("click", () => guard(() => saving(saveBtn, onSave)));
    return h("details", {}, h("summary", summaryAttrs, summaryText), h("div", { class: "tonal" }, body, h("div", { class: "actions" }, saveBtn, h("span", { class: "saved", role: "status" }))));
  }
  function sourcesEditor() {
    const roles = h("textarea", { class: "short", "data-test": "roles" }, prefs.targetRoles.join("\n"));
    const boards = h("textarea", { class: "short", "data-test": "boards" }, prefs.boards.join("\n"));
    const links = h("textarea", { class: "short" }, prefs.jobLinks.join("\n"));
    const locations = h("textarea", { class: "short" }, prefs.locations.join("\n"));
    const arr = ["remote", "hybrid", "onsite"].map((a) => h("input", { type: "checkbox", value: a, checked: prefs.workArrangements.includes(a) }));
    const body = h("div", { class: "grid2" },
      field("Job titles you want", roles, "One per line."),
      field("Employer boards", boards, "One per line, as provider:name from the board's web address: greenhouse:acme for boards.greenhouse.io/acme, lever:northwind for jobs.lever.co/northwind, ashby:civic-ledger for jobs.ashbyhq.com/civic-ledger. Add |Company for a friendlier name, e.g. lever:northwind|Northwind Health."),
      field("Single posting links", links),
      h("div", {}, field("Locations", locations, "One per line."),
        h("fieldset", {}, h("legend", {}, "Work arrangements you will take"), h("div", { class: "row" }, arr.map((c) => h("label", { class: "check compact" }, c, c.value))))));
    return editor("Edit what to search", { "data-test": "edit-sources" }, body, "Save search settings", { "data-test": "save-sources" }, async () => {
      const lines = (t) => t.value.split("\n").map((x) => x.trim()).filter(Boolean);
      Object.assign(prefs, { targetRoles: lines(roles), boards: lines(boards), jobLinks: lines(links), locations: lines(locations), workArrangements: arr.filter((c) => c.checked).map((c) => c.value) });
      await api.put("/api/preferences", prefs);
      refreshSummaries();
    });
  }
  function thresholds() {
    const strong = h("input", { type: "number", min: 0, max: 100, value: Math.round(prefs.screening.strongThreshold * 100) });
    const conditional = h("input", { type: "number", min: 0, max: 100, value: Math.round(prefs.screening.conditionalThreshold * 100) });
    const short = h("input", { type: "number", min: 0, max: 20, step: 0.5, value: prefs.screening.maxYearsShort });
    const draftFor = ["strong_fit", "conditional_fit", "stretch"].map((r) => h("input", { type: "checkbox", value: r, checked: prefs.materials.draftFor.includes(r) }));
    const body = h("div", { class: "grid2" },
      field("Share of required skills you have, for a strong fit (%)", strong),
      field("Share of required skills you have, for a conditional fit (%)", conditional),
      field("Years under the required experience that still count as conditional", short),
      h("fieldset", { class: "field" }, h("legend", {}, "Draft materials for"), draftFor.map((c) => h("label", { class: "check compact" }, c, FIT_LABEL[c.value][0].toLowerCase()))));
    return editor("Edit thresholds", {}, body, "Save thresholds", {}, async () => {
      prefs.screening = { ...prefs.screening, strongThreshold: Number(strong.value) / 100, conditionalThreshold: Number(conditional.value) / 100, maxYearsShort: Number(short.value) };
      prefs.materials = { ...prefs.materials, draftFor: draftFor.filter((c) => c.checked).map((c) => c.value) };
      await api.put("/api/preferences", prefs);
      refreshSummaries();
    });
  }
  function drafting() {
    const max = h("input", { type: "number", min: 0, max: 10, value: prefs.materials.maxNewDraftsPerRun });
    const wording = h("input", { type: "checkbox", checked: prefs.materials.modelCoverLetter, "data-test": "model-wording" });
    const problem = h("p", { class: "error-line", hidden: true });
    const body = h("div", { class: "grid2" },
      field("New drafts per search, at most", max),
      h("div", {}, h("label", { class: "check compact" }, wording, h("span", {}, "Let the private model on this server word my cover letters. ", h("span", { class: "hint" }, "Your résumé never leaves this server; every claim is still checked."))), problem));
    return editor("Edit drafting", {}, body, "Save drafting settings", {}, async () => {
      problem.hidden = true;
      try {
        if (wording.checked && !state.me.consents["model-wording"]) await api.post("/api/me/consent", { kinds: ["model-wording"] });
        prefs.materials = { ...prefs.materials, maxNewDraftsPerRun: Number(max.value), modelCoverLetter: wording.checked };
        await api.put("/api/preferences", prefs);
        state.me = await api.get("/api/me");
        refreshSummaries();
      } catch (e) { wording.checked = prefs.materials.modelCoverLetter; problem.textContent = e.message; problem.hidden = false; throw e; }
    });
  }

  // ── the palette: library search, detail, and the forge ──
  const search = h("input", { type: "search", placeholder: "Search steps, or describe one", "aria-label": "Search the step library", "data-test": "library-search" });
  const results = h("div", { class: "lib-list", "data-test": "library-results" });
  const detail = h("div", { class: "lib-detail", "data-test": "library-detail", hidden: true });
  const libNote = h("p", { class: "hint", hidden: true });
  const kindIcon = (kind) => h("span", { class: "kind" }, icon(kind === "check" ? "check-circle" : "bars"), vh(kind === "check" ? "check: " : "column: "));
  async function findSteps() {
    const query = search.value.trim();
    const items = await api.get("/api/library" + (query ? "?q=" + encodeURIComponent(query) : ""));
    clear(results); libNote.hidden = true;
    if (!state.meta.libraryReady) {
      // A new server certifies its starter steps on first start; look again shortly.
      results.append(h("p", { class: "hint warming", "data-test": "library-warming" }, h("span", { class: "dot small pulse accent", "aria-hidden": "true" }), "The starter steps are still being certified on this server; more appear here in a minute or two."));
      clearTimeout(findSteps.retry);
      findSteps.retry = setTimeout(async () => {
        if (!results.isConnected) return;
        state.meta = await api.get("/api/meta");
        guard(findSteps);
      }, 5000);
    } else if (items.length === 0) {
      libNote.hidden = false;
      if (query) {
        libNote.textContent = "No match. Describe it below and the studio makes one.";
        if (!request.value.trim()) setRequest(query);
      } else libNote.textContent = "The library on this server is empty. Describe a step below and the studio makes the first one.";
    }
    for (const item of items) {
      results.append(h("button", { class: "lib-item", type: "button", "data-test": `library-item-${item.id}`, onclick: () => guard(() => showDetail(item.id)) },
        kindIcon(item.kind),
        h("span", { class: "body" }, h("span", { class: "name" }, item.name), h("span", { class: "desc" }, item.description),
          item.visibility === "private" || item.status === "quarantined" ? h("span", { class: "tags tags" },
            item.visibility === "private" ? h("span", { class: "tag" }, icon("lock"), "private") : null,
            item.status === "quarantined" ? h("span", { class: "tag bad" }, "switched off") : null) : null),
        h("span", { class: "right" }, item.uses > 0 ? h("span", {}, `${item.uses} use${item.uses === 1 ? "" : "s"}`) : null),
        h("span", { class: "chev" }, icon("chevron-right"))));
    }
  }
  function withSettingLabels(text, settings) {
    let out = text || "";
    settings.forEach((s, i) => { out = out.replaceAll(`setting${i + 1}`, `“${s.label}”`); });
    return out;
  }
  function exampleSentence(e, kind) {
    const i = e.input, o = e.output;
    const where = [i.location, i.arrangement].filter(Boolean).join(", ");
    const settings = [i.setting1, i.setting2, i.setting3].filter(Boolean).map((v) => `“${v}”`).join(", ");
    const left = `“${i.title || "a posting"}”${where ? `, ${where}` : ""}${settings ? ` with ${settings}` : ""}`;
    const right = kind === "column" ? (o.value || "––") : `${o.verdict}${o.note ? `: ${o.note}` : ""}`;
    return `${left} → ${right}`;
  }
  async function showDetail(id) {
    const item = await api.get(`/api/library/${encodeURIComponent(id)}`);
    results.hidden = true; libNote.hidden = true;
    add(clear(detail),
      h("button", { class: "btn ghost small", type: "button", onclick: () => { detail.hidden = true; results.hidden = false; } }, icon("arrow-left"), "Back to results"),
      h("h3", {}, item.name),
      h("p", { class: "desc" }, item.description),
      h("div", { class: "actions" },
        h("button", { class: "btn primary", "data-test": "add-step", disabled: item.status === "quarantined", onclick: () => {
          addStep(item); detail.hidden = true; results.hidden = false;
        } }, "Add to my pipeline")),
      item.status === "quarantined" ? h("p", { class: "error-line" }, item.statusDetail || "Switched off for everyone until an operator reviews it.") : null,
      h("p", { class: "caption mt3" }, `Checked by Ashlar · its tests caught ${item.mutants} planted bugs · ${item.writtenBy === "seed" ? "starter step" : item.writtenBy === "claude" ? "written by Claude from a description with no personal data" : "written on this server"} · `,
        h("span", { class: "mono", title: item.contentHash }, `code id ${item.contentHash.slice(0, 10)}…`)),
      h("details", {}, h("summary", {}, "Exactly what it does"), h("p", { class: "meaning" }, withSettingLabels(item.behavior, item.settings))),
      item.examples?.length ? h("details", {}, h("summary", {}, "What it does on made-up postings"),
        h("ul", { class: "list" }, item.examples.slice(0, 3).map((e) => h("li", {}, exampleSentence(e, item.kind))))) : null);
    detail.hidden = false;
  }
  search.addEventListener("input", () => { clearTimeout(search.t); search.t = setTimeout(() => guard(findSteps), 250); });
  search.addEventListener("keydown", (e) => { if (e.key === "Escape") { search.value = ""; guard(findSteps); } });
  document.addEventListener("keydown", (e) => {
    if (e.key === "/" && !/^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName || "")) { e.preventDefault(); search.focus(); }
  }, { signal });

  const request = h("textarea", { class: "short", placeholder: "e.g. Exclude anything that mentions on-call or weekend shifts", "aria-label": "Describe a new step", "data-test": "forge-request", maxlength: 2000 });
  const keepPrivate = h("input", { type: "checkbox", "data-test": "forge-private" });
  const progress = h("div", { "data-test": "forge-progress" });
  const allowance = h("span", { class: "caption", "data-test": "allowance" });
  const forgeProblem = h("p", { class: "error-line", hidden: true });
  const ask = h("button", { class: "btn", "data-test": "forge-submit" }, "Make this step");
  function setAllowance(used, limit) {
    allowance.textContent = `${used} of ${limit} new steps used this month · adding a library step doesn't count`;
    const exhausted = used >= limit;
    ask.disabled = exhausted;
    allowance.textContent = exhausted ? `You've used all ${limit} new steps for this month. Adding a step from the library still works and doesn't count.` : allowance.textContent;
  }
  setAllowance(forge.used, forge.limit);
  function setRequest(text) { request.value = text; request.dispatchEvent(new Event("input")); }
  request.addEventListener("input", () => { forgeProblem.hidden = true; ask.classList.toggle("primary", request.value.trim().length > 0); lastRequest = request.value; persist(); });
  if (lastRequest) { request.value = lastRequest; ask.classList.add("primary"); }
  ask.addEventListener("click", () => guard(async () => {
    if (!request.value.trim()) { forgeProblem.textContent = "Describe what the step should do."; forgeProblem.hidden = false; request.focus(); return; }
    forgeProblem.hidden = true;
    ask.disabled = true; ask.setAttribute("aria-busy", "true");
    try {
      const { id } = await api.post("/api/forge", { request: request.value, private: keepPrivate.checked });
      await follow(id, Date.now());
    } catch (e) { forgeProblem.textContent = e.message; forgeProblem.hidden = false; }
    finally { ask.disabled = false; ask.removeAttribute("aria-busy"); }
  }));
  function failedAt(job) {
    const text = job.log.join("\n").toLowerCase();
    return text.includes("certif") ? 2 : text.includes("writ") || text.includes("attempt") ? 1 : 0;
  }
  function renderForge(job, startedAt) {
    const active = job.status === "failed" ? failedAt(job) : FORGE_INDEX[job.status] ?? 0;
    const elapsed = Math.max(0, Math.round((Date.now() - startedAt) / 1000));
    const list = h("ol", { class: "forge" }, FORGE_STEPS.map(([title, past, caption], i) => {
      const done = i < active || job.status === "done";
      const isActive = i === active && job.status !== "done";
      const marker = done ? icon("check") : h("span", { class: `dot${isActive ? ` accent${ACTIVE_FORGE.has(job.status) ? " pulse" : ""}` : " hollow"}`, "aria-hidden": "true" });
      return h("li", { class: done ? "done" : isActive ? "active" : "" },
        h("span", { class: "marker" }, marker),
        h("div", {}, h("div", { class: "t" }, done ? past : title),
          isActive && job.log.length ? h("div", { class: "c" }, job.log[job.log.length - 1]) : caption ? h("div", { class: "c" }, caption) : null,
          isActive && job.status === "failed" ? h("p", { class: "error-line mt1" }, job.error || "Stopped.") : null));
    }));
    return h("div", {},
      h("div", { class: "forge-head" }, "Your request", h("span", { class: "mono" }, "status: ", h("span", { "data-test": "forge-status" }, job.status)),
        h("span", { class: "elapsed num" }, elapsed < 60 ? `Started ${elapsed} s ago` : `Started ${Math.round(elapsed / 60)} min ago`)),
      list,
      job.status === "needs-input" ? h("div", { class: "tonal mt3" }, h("p", { class: "small" }, h("strong", {}, "One question: "), job.question || ""), h("p", { class: "hint mt1" }, "Add the detail to your request above and send it again.")) : null,
      job.status === "failed" ? h("div", { class: "actions" }, h("button", { class: "btn small", onclick: () => ask.click(), disabled: !request.value.trim() }, "Try again")) : null,
      job.log.length > 1 ? h("details", {}, h("summary", {}, "Details"), h("ol", { class: "list" }, job.log.map((l) => h("li", {}, l)))) : null);
  }
  async function follow(id, startedAt) {
    pipeline.classList.add("forging");
    try {
      for (;;) {
        if (signal.aborted) return;
        const job = await api.get(`/api/forge/${id}`);
        const started = job.createdAt ? Date.parse(job.createdAt) || startedAt : startedAt;
        clear(progress).append(renderForge(job, started));
        const said = job.status === "failed" ? `Stopped: ${job.error || ""}` : job.status === "needs-input" ? `One question: ${job.question || ""}` : FORGE_STEPS[FORGE_INDEX[job.status] ?? 0][0];
        if (said !== follow.said) { follow.said = said; announce(said); }
        if (["done", "failed", "needs-input"].includes(job.status)) {
          if (job.status === "needs-input" && lastRequest && !request.value.trim()) setRequest(lastRequest);
          if (job.status === "done") {
            const fresh = await api.get("/api/workflow");
            // Keep unsaved edits; take the new step the forge added, where the user asked for it.
            const known = new Set(steps.map((s) => s.key));
            let newKey = null;
            for (const s of fresh.steps) if (!known.has(s.key)) { steps.splice(insertAt ?? steps.length, 0, { ...s, settings: [...s.settings] }); newKey = s.key; }
            insertAt = null;
            workflow.steps = fresh.steps;
            lastRequest = ""; request.value = ""; ask.classList.remove("primary"); persist();
            redrawSteps(); await findSteps();
            const f = await api.get("/api/forge"); setAllowance(f.used, f.limit);
            if (!(newKey && highlight(newKey))) toast(job.reused ? "Added a step from the library — no new code needed." : "Your new step is certified and in your pipeline.");
          }
          return;
        }
        await new Promise((r) => setTimeout(r, 1500));
      }
    } finally { pipeline.classList.remove("forging"); }
  }

  side.append(
    h("h2", {}, "Add a step"),
    search, results, libNote, detail,
    h("div", { class: "describe" },
      h("h3", {}, "Or describe a new step"),
      request,
      h("label", { class: "check compact" }, keepPrivate, h("span", {}, "Keep it private to me (others won't reuse it)")),
      h("div", { class: "row" }, ask, allowance),
      forgeProblem,
      h("details", { class: "mt1" }, h("summary", {}, "Where your words go"), h("p", { class: "hint" }, "Your words are read by the model on this server. If new code is needed, a plain description of the rule, with nothing about you, goes to Claude, an AI service outside this server; Ashlar tests the code before it can run. If the description cannot be made free of your details, the step is written here and stays private to you.")),
      progress));

  // The one palette node lives beside the pipeline on wide screens and under stage 4 on narrow ones.
  const wide = matchMedia("(min-width: 960px)");
  const place = () => { const target = wide.matches ? grid : paletteSlot; if (side.parentNode !== target) target.append(side); };
  wide.addEventListener("change", place, { signal });

  redrawSteps(); drawSave(); place();
  await findSteps();
  const running = forge.jobs.find((j) => ACTIVE_FORGE.has(j.status));
  if (running) follow(running.id, Date.parse(running.createdAt) || Date.now());
}

function wiring(graph) {
  // Layered layout: each node sits one column right of its furthest predecessor; a single column below 720px.
  const level = new Map(graph.nodes.map((n) => [n.id, 0]));
  for (let pass = 0; pass < graph.nodes.length; pass++)
    for (const e of graph.edges) level.set(e.to, Math.max(level.get(e.to), level.get(e.from) + 1));
  const brickNodes = graph.nodes.filter((n) => n.type === "brick");
  // One vertical chain at every width: the pipeline reads top to bottom, so does its wiring.
  const cols = new Map();
  for (const n of brickNodes) { const l = level.get(n.id); if (!cols.has(l)) cols.set(l, []); cols.get(l).push(n); }
  const ordered = [...cols.keys()].sort((a, b) => a - b).flatMap((l) => cols.get(l));
  const longest = Math.max(12, ...ordered.map((n) => (n.name || n.id).length));
  const W = Math.min(360, 64 + Math.ceil(longest * 7.2)), H = 44, GY = 20;
  const pos = new Map();
  ordered.forEach((n, row) => pos.set(n.id, { x: 20, y: 20 + row * (H + GY), row }));
  const width = 40 + W + 40, height = 40 + ordered.length * (H + GY) - GY;
  const svg = h("svg", { svg: true, width, height, viewBox: `0 0 ${width} ${height}`, role: "img", "data-test": "wiring",
    "aria-label": `How your pipeline is wired, ${ordered.length} steps in order: ${ordered.map((n) => n.name || n.id).join(", ")}` });
  const seen = new Set();
  for (const e of graph.edges) {
    const a = pos.get(e.from), b = pos.get(e.to);
    if (!a || !b || seen.has(e.from + ">" + e.to)) continue;
    seen.add(e.from + ">" + e.to);
    if (b.row === a.row + 1) {
      const x = a.x + 24, y1 = a.y + H, y2 = b.y;
      svg.append(h("path", { svg: true, class: "edge", d: `M${x},${y1} L${x},${y2}` }));
    } else {
      // A connection that skips rows bows out to the right of the chain.
      const x = a.x + W, y1 = a.y + H / 2, y2 = b.y + H / 2, bow = x + 28;
      svg.append(h("path", { svg: true, class: "edge", d: `M${x},${y1} C${bow},${y1} ${bow},${y2} ${x},${y2}` }));
    }
  }
  for (const n of brickNodes) {
    const p = pos.get(n.id);
    const user = n.brickId?.startsWith("step.");
    const certified = n.brickId?.startsWith("jobsearch.recommendation") || n.brickId === "jobsearch.validate-materials";
    const g = h("g", { svg: true, class: `node${user ? " user" : certified ? " certified" : ""}` },
      h("title", { svg: true }, `${n.name || n.id} · ${n.brickId || ""}`),
      h("rect", { svg: true, x: p.x, y: p.y, width: W, height: H, rx: 6 }),
      h("text", { svg: true, x: p.x + 12, y: p.y + 27 }, n.name || n.id));
    if (user) g.append(h("text", { svg: true, class: "side-label", x: p.x + W - 12, y: p.y + 27, "text-anchor": "end" }, "yours"));
    if (certified) g.append(h("path", { svg: true, class: "mark-check", d: `M${p.x + W - 22},${p.y + 22} l3 3 6-7` }));
    svg.append(g);
  }
  return h("div", { class: "wiring" }, svg);
}

// ── shared table: real rows for the tests, labelled cells for phones ───────────────────────────────────
function table({ test, columns, rows, stack = true }) {
  return h("table", { "data-test": test, class: stack ? "stack" : null, role: "table" },
    h("thead", { role: "rowgroup" }, h("tr", { role: "row" }, columns.map((c) => h("th", { role: "columnheader", scope: "col", class: [c.align === "right" ? "num" : null, c.fit ? "fit" : null].filter(Boolean).join(" ") || null }, c.label)))),
    h("tbody", { role: "rowgroup" }, rows.flatMap((r) => [
      h("tr", { role: "row", class: r.cls }, r.cells.map((cell, i) => h("td", { role: "cell", "data-label": columns[i].label, class: [columns[i].align === "right" ? "num" : null, columns[i].title ? "title-cell" : null, columns[i].fit ? "fit" : null].filter(Boolean).join(" ") || null }, cell))),
      r.detail ? h("tr", { role: "row", class: "detail", id: r.detailId, hidden: true }, h("td", { role: "cell", colspan: columns.length, "data-nolabel": "" }, r.detail)) : null,
    ])));
}
function expander(detailRow, title) {
  const chev = icon("chevron-down");
  const btn = h("button", { class: "btn icon expand", "aria-expanded": "false", "aria-controls": detailRow.id, "aria-label": `Details for ${title}` }, chev, h("span", { class: "label" }, "Details"));
  btn.addEventListener("click", () => {
    const open = detailRow.hidden;
    detailRow.hidden = !open;
    btn.setAttribute("aria-expanded", String(open));
  });
  return btn;
}
const dash = () => h("span", { class: "dash" }, "––");
function statusWord(label, tone) { return h("span", { class: "status" }, h("span", { class: `dot ${tone}`, "aria-hidden": "true" }), h("span", { class: "word" }, label)); }

// ── tracker ────────────────────────────────────────────────────────────────────────────────────────────
async function tracker(main) {
  const jobs = await api.get("/api/tracker");
  main.append(h("div", { class: "page-head" }, h("div", {}, h("h1", {}, "Tracker"),
    h("p", { class: "lede" }, "The same jobs as the sheet in your Drive. Fit is decided by fixed, tested rules on this server. Only you can mark a job applied, interviewing, offer or rejected, and you say what happened when you do."))));
  if (jobs.length === 0) {
    const go = h("button", { class: "btn primary", "data-test": "run-now-tracker", onclick: () => guard(async () => { go.disabled = true; await api.post("/api/runs"); location.hash = "#/runs"; }) }, "Run a search now");
    return main.append(h("div", { class: "empty" }, h("strong", {}, "No jobs yet"),
      h("p", {}, "A search reads your boards and puts what it finds here, with a fit verdict for each posting. Your daily search runs on its own; you can also run one now."),
      h("div", { class: "actions" }, go, h("a", { class: "btn ghost", href: "#/designer" }, "Edit what to search"))));
  }
  const group = (status) => STATUS_GROUPS.findIndex(([, list]) => list.includes(status));
  const fitRank = { strong_fit: 0, conditional_fit: 1, stretch: 2, not_currently_eligible: 3 };
  const order = (a, b) => ((group(a.status) === 1 ? 0 : group(a.status) === 0 ? 1 : 2) - (group(b.status) === 1 ? 0 : group(b.status) === 0 ? 1 : 2)) || ((fitRank[a.fit] ?? 4) - (fitRank[b.fit] ?? 4));
  let filter = "all";
  const chips = h("div", { class: "chips", role: "group", "aria-label": "Show" });
  const holder = h("div");
  function drawChips() {
    clear(chips);
    const counts = { all: jobs.length, 1: jobs.filter((j) => group(j.status) === 1).length, 0: jobs.filter((j) => group(j.status) === 0).length, 2: jobs.filter((j) => group(j.status) === 2).length };
    for (const [key, label] of [["all", "All"], ["1", "In progress"], ["0", "Not yet"], ["2", "Closed"]])
      chips.append(h("button", { class: "btn small ghost", "aria-pressed": String(filter === key), onclick: () => { filter = key; drawChips(); drawTable(); } }, `${label} ${counts[key]}`));
  }
  function drawTable() {
    const shown = jobs.filter((j) => filter === "all" || String(group(j.status)) === filter).sort(order);
    const rows = shown.map((j) => {
      const title = h("div", { class: "title" }, j.url
        ? h("a", { href: j.url, target: "_blank", rel: "noopener noreferrer", "aria-label": `${j.title} at ${j.employer}, opens the posting in a new tab` }, j.title, icon("external"))
        : j.title);
      const meta = [j.employer, j.location, j.arrangement && !(j.location || "").toLowerCase().includes(j.arrangement.toLowerCase()) ? j.arrangement : null].filter(Boolean).join(" · ");
      const jobCell = h("div", {}, title, h("div", { class: "meta" }, meta),
        j.listing === "closed" ? h("div", { class: "tag mt-half" }, "Listing closed") : j.listing === "stale" ? h("div", { class: "tag mt-half" }, "Not checked lately") : null);
      const fit = FIT_LABEL[j.fit];
      const fitCell = h("div", {}, fit ? statusWord(fit[0], fit[1]) : dash(),
        j.excludedBy ? h("div", { class: "tag bad mt-half" }, `Excluded by “${j.excludedBy}”`) : null,
        j.reason ? h("div", { class: "reason" }, REASON_LABEL[j.reason] ?? j.reason) : null);
      // Status: a native select; an assertion (applied, interviewing, offer, rejected) asks what happened first.
      const status = h("select", { "aria-label": `Status of ${j.title}`, "data-test": `status-${j.id}` },
        STATUS_GROUPS.map(([label, list]) => h("optgroup", { label }, list.map((s) => h("option", { value: s }, STATUS_LABEL[s])))));
      status.value = j.status;
      const evidenceBox = h("div", { class: "tonal evidence", hidden: true });
      const caption = h("div", { class: "caption mt1" }, j.status === "applied" && j.appliedDate ? ["applied ", h("time", { datetime: j.appliedDate }, relativeDay(j.appliedDate))] : null);
      const saveStatus = (evidence, button) => guard(async () => {
        const work = async () => { await api.post(`/api/jobs/${encodeURIComponent(j.id)}/status`, { status: status.value, evidence }); j.status = status.value; };
        if (button) await saving(button, work); else await work();
        if (evidenceBox.contains(document.activeElement)) status.focus({ preventScroll: true });
        clear(evidenceBox).hidden = true; toast("Status saved."); drawChips();
      });
      // Arrow keys fire change on every step through the list: wait for the person to settle before saving.
      let viaKeys = false;
      status.addEventListener("keydown", (e) => { viaKeys = /^(Arrow|Home|End)/.test(e.key); });
      status.addEventListener("mousedown", () => { viaKeys = false; });
      status.addEventListener("change", () => {
        clearTimeout(status.t);
        if (!ASSERTIONS.has(status.value)) { status.t = setTimeout(() => saveStatus(null), viaKeys ? 700 : 0); return; }
        const what = h("input", { type: "text", placeholder: "e.g. Confirmation email, today", "aria-label": "What happened", "data-test": `evidence-${j.id}` });
        const cancel = () => { if (evidenceBox.contains(document.activeElement)) status.focus({ preventScroll: true }); status.value = j.status; clear(evidenceBox).hidden = true; };
        what.addEventListener("keydown", (e) => { if (e.key === "Escape") cancel(); });
        const saveBtn = h("button", { class: "btn small primary", "data-test": `evidence-save-${j.id}`, onclick: () => what.value.trim() ? saveStatus(what.value.trim(), saveBtn) : toast("Say what happened.", { error: true }) }, "Save");
        clear(evidenceBox).hidden = false;
        evidenceBox.append(h("label", { for: `ev-${j.id}` }, "What happened?"), h("p", { class: "hint mb2" }, "Only you can say."), what,
          h("div", { class: "actions mt2" }, saveBtn, h("button", { class: "btn small ghost", onclick: cancel }, "Cancel")));
        what.id = `ev-${j.id}`;
        if (!viaKeys) what.focus();
      });
      const statusCell = h("div", {}, status, caption, evidenceBox);
      // Detail: what the user's steps said, what the posting asks for, and the user's own notes.
      const notes = h("input", { type: "text", value: j.notes || "", placeholder: "Anything you want to remember" });
      const next = h("input", { type: "text", value: j.nextSteps || "", placeholder: "e.g. Follow up Friday" });
      const noteSaved = h("span", { class: "saved", role: "status" });
      const saveNotes = () => guard(async () => {
        if (notes.value === (j.notes || "") && next.value === (j.nextSteps || "")) return;
        await api.post(`/api/jobs/${encodeURIComponent(j.id)}/notes`, { notes: notes.value, nextSteps: next.value });
        j.notes = notes.value; j.nextSteps = next.value;
        noteSaved.textContent = "Saved"; clearTimeout(noteSaved.timer); noteSaved.timer = setTimeout(() => (noteSaved.textContent = ""), 2000);
      });
      notes.addEventListener("blur", saveNotes); next.addEventListener("blur", saveNotes);
      const detail = h("div", { class: "detail-grid" },
        j.steps?.length ? h("div", {}, h("div", { class: "eyebrow mb2" }, "Your steps said"),
          h("dl", { class: "kv" }, j.steps.flatMap((s) => [h("dt", {}, s.name), h("dd", {}, statusWord(s.text || (s.kind === "column" ? "––" : s.verdict), s.verdict === "exclude" ? "bad" : s.verdict === "flag" ? "warn" : s.kind === "column" ? "muted" : "good"))]))) : null,
        j.gaps?.length ? h("div", {}, h("div", { class: "eyebrow mb1" }, "Asked for, not shown in your résumé"), h("p", { class: "meaning" }, j.gaps.join(", "))) : null,
        j.unknowns?.length ? h("div", {}, h("div", { class: "eyebrow mb1" }, "Couldn't tell"), h("p", { class: "meaning" }, j.unknowns.join(", "))) : null,
        h("div", { class: "grid2" }, field("Notes", notes), field("Next step", next)), noteSaved);
      const detailId = `detail-${j.id}`;
      const row = { cls: "expandable", cells: [jobCell, fitCell, statusCell, null], detail, detailId };
      return row;
    });
    const columns = [{ label: "Job", title: true }, { label: "Fit" }, { label: "Status" }, { label: "", align: "right" }];
    const t = table({ test: "tracker", columns, rows });
    // The expand button needs its detail row's id: wire it after the rows exist.
    for (const tr of t.querySelectorAll("tbody tr.expandable")) {
      const detailRow = tr.nextElementSibling;
      const name = tr.querySelector(".title")?.textContent || "job";
      tr.lastElementChild.append(expander(detailRow, name));
      tr.lastElementChild.setAttribute("data-nolabel", "");
    }
    clear(holder).append(t);
  }
  drawChips(); drawTable();
  main.append(chips, holder);
}

// ── runs ───────────────────────────────────────────────────────────────────────────────────────────────
function when(iso) {
  const d = new Date(iso), now = new Date();
  const sameDay = (a, b) => a.toDateString() === b.toDateString();
  const yesterday = new Date(now); yesterday.setDate(now.getDate() - 1);
  const hm = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
  const label = sameDay(d, now) ? `Today ${hm}` : sameDay(d, yesterday) ? `Yesterday ${hm}`
    : d.getFullYear() === now.getFullYear() ? `${d.toLocaleDateString([], { day: "numeric", month: "short" })}, ${hm}` : d.toLocaleDateString([], { day: "numeric", month: "short", year: "numeric" });
  return h("time", { datetime: iso, title: d.toLocaleString() }, label);
}
function duration(start, end) {
  if (!end) return "";
  const s = Math.max(0, Math.round((new Date(end) - new Date(start)) / 1000));
  return s < 60 ? ` · ${s} s` : ` · ${Math.round(s / 60)} min`;
}
const plural = (n, one, many = one + "s") => `${n} ${n === 1 ? one : many}`;
/** "today", "yesterday", "3 days ago", or a short date, for a YYYY-MM-DD. */
function relativeDay(ymd) {
  const d = new Date(ymd + "T00:00:00"), now = new Date();
  const days = Math.round((new Date(now.getFullYear(), now.getMonth(), now.getDate()) - d) / 86400000);
  return days <= 0 ? "today" : days === 1 ? "yesterday" : days < 14 ? `${days} days ago` : d.toLocaleDateString([], { day: "numeric", month: "short" });
}

async function runs(main, _query, signal) {
  let list = await api.get("/api/runs");
  const go = h("button", { class: "btn primary", "data-test": "run-now", onclick: () => guard(async () => {
    if (go.getAttribute("aria-busy") === "true") return;
    go.setAttribute("aria-busy", "true");
    try { await api.post("/api/runs"); } finally { go.removeAttribute("aria-busy"); }
    toast("Search started. It reads at most a few dozen postings, at human speed.");
    poll();
  }) }, "Run a search now");
  const holder = h("div");
  main.append(h("div", { class: "page-head" }, h("div", {}, h("h1", {}, "Runs"), h("p", { class: "lede" }, "One row per search: what it read, what it found, and what it could not finish.")), go), holder);
  const autoOpened = new Set();
  function draw() {
    // Redraw only when something changed, and keep open rows and focus where the person left them.
    const json = JSON.stringify(list);
    if (json === draw.last) return;
    draw.last = json;
    const open = new Set([...holder.querySelectorAll("tr.detail:not([hidden])")].map((r) => r.id));
    const focused = document.activeElement?.getAttribute("aria-controls");
    const running = list.some((r) => r.status === "running");
    go.setAttribute("aria-busy", String(running));
    go.replaceChildren(running ? "Running…" : "Run a search now");
    clear(holder);
    if (list.length === 0) return holder.append(h("div", { class: "empty" }, h("strong", {}, "No searches yet"), h("p", {}, "A search reads at most a few dozen postings from your boards, at human speed, and reports here what it read and what it could not finish.")));
    const sorted = [...list].sort((a, b) => (a.status === "running" ? -1 : 0) - (b.status === "running" ? -1 : 0));
    const rows = sorted.map((r) => {
      const [label, tone] = RUN_LABEL[r.status] || [r.status, "muted"];
      const s = r.summary || {};
      const what = h("div", {});
      if (r.status === "blocked_auth") add(what, "Google access expired. ", state.meta.googleSignIn ? h("a", { class: "btn small ml1", href: "/auth/google/start" }, "Reconnect Google") : null);
      else if (r.error && !s.postingsRead) what.append(r.error);
      else {
        what.append(`Read ${plural(s.postingsRead ?? 0, "posting")} · ${s.newJobs ?? 0} new · ${(s.drafted || []).length} drafted`);
        if (s.couldNotComplete?.length) what.append(" · ", h("span", { class: "bad" }, `${s.couldNotComplete.length} could not complete`));
        if (s.deferred) what.append(` · ${s.deferred} left for the next search`);
        if (s.reason) what.append(` · ${s.reason}`);
      }
      const detailParts = [];
      const boards = Object.entries(s.boards || {});
      if (boards.length) detailParts.push(h("div", {}, h("div", { class: "eyebrow mb1" }, "Boards"), h("dl", { class: "kv" }, boards.flatMap(([b, v]) => [h("dt", {}, b.split("|").pop()), h("dd", {}, String(v))]))));
      if (s.statusChanges?.length) detailParts.push(h("div", {}, h("div", { class: "eyebrow mb1" }, "Listing changes"), h("ul", { class: "list" }, s.statusChanges.map((x) => h("li", {}, x)))));
      if (s.couldNotComplete?.length) detailParts.push(h("div", {}, h("div", { class: "eyebrow mb1" }, "Could not complete"), h("ul", { class: "list" }, s.couldNotComplete.map((x) => h("li", {}, x)))));
      if (r.error) detailParts.push(h("p", { class: "error-line" }, r.error));
      if (s.workflowLog?.length) detailParts.push(h("div", {}, h("div", { class: "eyebrow mb1" }, "Search log"), h("pre", { class: "log" }, s.workflowLog.join("\n"))));
      const detail = detailParts.length ? h("div", { class: "detail-grid tight" }, detailParts) : null;
      const dot = statusWord(label, tone);
      if (r.status === "running") dot.firstChild.classList.add("pulse");
      return { cls: detail ? "expandable" : null, cells: [dot, h("span", { class: "num" }, when(r.startedAt), duration(r.startedAt, r.finishedAt)), what, null], detail, detailId: `run-${r.id}`, label, run: r };
    });
    const t = table({ test: "runs", columns: [{ label: "Status", fit: true }, { label: "When", fit: true }, { label: "What happened" }, { label: "", align: "right" }], rows });
    const expandable = rows.filter((r) => r.detail);
    [...t.querySelectorAll("tbody tr.expandable")].forEach((tr, i) => {
      const detailRow = tr.nextElementSibling, r = expandable[i];
      tr.lastElementChild.append(expander(detailRow, `the ${r.label.toLowerCase()} run, ${when(r.run.startedAt).textContent}`));
      tr.lastElementChild.setAttribute("data-nolabel", "");
      if ((r.run.status === "failed" || r.run.status === "partial") && !autoOpened.has(r.run.id)) { autoOpened.add(r.run.id); open.add(detailRow.id); }
    });
    holder.append(t);
    for (const id of open) holder.querySelector(`[aria-controls="${id}"][aria-expanded="false"]`)?.click();
    if (focused) holder.querySelector(`[aria-controls="${focused}"]`)?.focus({ preventScroll: true });
  }
  let timer = null;
  async function poll() {
    clearTimeout(timer);
    if (signal.aborted) return;
    list = await api.get("/api/runs");
    draw();
    if (list.some((r) => r.status === "running")) timer = setTimeout(() => guard(poll), 4000);
  }
  signal.addEventListener("abort", () => clearTimeout(timer));
  draw();
  if (list.some((r) => r.status === "running")) timer = setTimeout(() => guard(poll), 4000);
}

// ── profile ────────────────────────────────────────────────────────────────────────────────────────────
async function profile(main) {
  const p = await api.get("/api/profile");
  main.append(h("div", { class: "page-head" }, h("div", {}, h("h1", {}, "Profile"),
    h("p", { class: "lede" }, p.hasResume ? "What the studio read from your résumé, for you to confirm, and the things only you can say." : "Start with your résumé. The studio reads a profile from it for you to confirm; nothing is invented and what it does not say stays unknown."))));

  const fieldsSection = h("section", { class: "section" });
  function drawFields() {
    const fields = p.fields.filter((f) => !USER_ONLY.some((u) => u.name === f.name));
    const confirmed = fields.filter((f) => f.status === "confirmed").length;
    const pending = fields.filter((f) => f.status === "derived" && f.value);
    const confirmAll = h("button", { class: "btn", "data-test": "profile-confirm-all", disabled: pending.length === 0 }, "Confirm all");
    confirmAll.addEventListener("click", () => guard(() => saving(confirmAll, async () => {
      for (const f of pending) await api.post("/api/profile/review", { field: f.name, confirm: true });
      Object.assign(p, await api.get("/api/profile")); drawFields();
    }, "Confirmed")));
    const rows = fields.map((f) => {
      const label = FIELD_LABEL[f.name] || f.name.replaceAll("_", " ");
      const review = (confirm) => guard(async () => { await api.post("/api/profile/review", { field: f.name, confirm }); Object.assign(p, await api.get("/api/profile")); drawFields(); });
      const status = f.status === "confirmed" ? statusWord("Confirmed", "good") : f.status === "rejected" ? statusWord("Marked not right", "bad") : f.status === "unknown" || !f.value ? statusWord("Not found in résumé", "muted") : statusWord("From résumé", "muted");
      const actions = h("div", { class: "row-actions" },
        f.status !== "confirmed" && f.value ? h("button", { class: "btn small", "data-test": `field-confirm-${f.name}`, "aria-label": `Confirm ${label}`, onclick: () => review(true) }, "Confirm") : null,
        f.status !== "rejected" && f.value ? h("button", { class: "btn small ghost", "data-test": `field-reject-${f.name}`, "aria-label": `Mark ${label} as not right`, onclick: () => review(false) }, "Not right") : null);
      return { cls: "quiet-actions", cells: [h("span", { class: "title small" }, label), f.value ? h("span", { title: f.value }, f.value.slice(0, 120)) : dash(), h("span", { class: "caption" }, f.evidence || f.source || ""), status, actions] };
    });
    clear(fieldsSection).append(
      h("div", { class: "section-head" }, h("div", {}, h("h2", {}, "What the studio knows about you"), h("p", { class: "caption" }, `${plural(fields.length, "field")} from your résumé · ${confirmed} confirmed`)), h("div", { class: "row" }, confirmAll, h("span", { class: "saved", role: "status" }))),
      table({ test: "profile-fields", columns: [{ label: "Field", title: true }, { label: "Value" }, { label: "From" }, { label: "Status" }, { label: "", align: "right" }], rows }));
  }

  function userOnly() {
    const controls = USER_ONLY.map((u) => {
      const current = p.fields.find((f) => f.name === u.name)?.value || "";
      let input;
      if (u.type === "yesno") input = h("select", { "data-test": `field-${u.name}` }, h("option", { value: "" }, "—"), h("option", { value: "yes" }, "Yes"), h("option", { value: "no" }, "No"));
      else if (u.type === "number") input = h("input", { type: "number", min: 0, max: 100, "data-test": `field-${u.name}` });
      else if (u.type === "date") input = h("input", { type: "date", "data-test": `field-${u.name}` });
      else input = h("input", { type: "text", placeholder: u.placeholder, "data-test": `field-${u.name}` });
      input.value = current;
      return { u, input, initial: current };
    });
    const saveBtn = h("button", { class: "btn", "aria-label": "Save the fields only you can say" }, "Save these");
    saveBtn.addEventListener("click", () => guard(() => saving(saveBtn, async () => {
      for (const c of controls) if (c.input.value !== c.initial) { await api.post("/api/profile/field", { field: c.u.name, value: c.input.value || null }); c.initial = c.input.value; }
    })));
    return h("section", { class: "section" },
      h("h2", {}, "Things only you can say"),
      h("p", { class: "meaning section-lede" }, "These stay on this server and are never guessed from your résumé. Leave one blank and postings that need it will say so."),
      h("div", { class: "grid2" }, controls.map((c) => field(c.u.label, c.input, c.u.hint))),
      h("div", { class: "actions" }, saveBtn, h("span", { class: "saved", role: "status" })));
  }

  const text = h("textarea", { placeholder: "Paste your résumé as plain text", "aria-label": "Your résumé as plain text", "data-test": "resume-text", class: p.hasResume ? "short" : "tall" });
  const file = h("input", { type: "file", class: "vh", accept: ".txt,.md,text/plain" });
  const fileName = h("span", { class: "caption" });
  file.addEventListener("change", async () => { if (file.files[0]) { text.value = await file.files[0].text(); fileName.textContent = file.files[0].name; } });
  const resume = h("section", { class: "section" },
    h("h2", {}, p.hasResume ? "Replace your résumé" : "Add your résumé"),
    h("p", { class: "meaning section-lede" }, "Paste it as plain text, or choose a .txt or .md file. PDF and Word files are not read yet: open your résumé, select all, copy, and paste it here. Nothing is invented from it; what it does not say stays unknown."),
    text,
    h("div", { class: "actions" },
      h("button", { class: "btn primary", "data-test": "resume-save", onclick: () => guard(async () => {
        await api.post("/api/profile/resume", { filename: file.files[0]?.name || "resume.txt", text: text.value });
        toast("Résumé saved. Review the fields below."); await route();
      }) }, "Save résumé"),
      h("label", { class: "btn" }, "Choose a .txt file", file), fileName));

  if (!p.hasResume) return main.append(resume);
  drawFields();
  main.append(fieldsSection, userOnly(), resume);
}

// ── privacy & data ─────────────────────────────────────────────────────────────────────────────────────
async function privacy(main) {
  const signedIn = !!state.me;
  const meta = state.meta;
  const header = h("p", { class: "caption mt2" }, `Notice version ${meta.noticeVersion}`, meta.synthetic ? " · Operator: not set (synthetic environment)" : "");
  const rows = [
    ["Your résumé, profile, preferences, tracker", "This server's database, and the files you asked for in your own Google Drive."],
    ["Anything you type to describe a step", `Read only by the model on this server (${meta.privateModel}). It is discarded when the request finishes.`],
    ["New step code", `Written by ${meta.stepWriter} from a generic description that passed a personal-data check, then certified by Ashlar. Shared steps contain no one's details.`],
    ["Model-worded cover letters (optional)", "Only on this server's model, only if you turn it on."],
    ["How long we keep things", "Jobs whose listing closed: 365 days, or for good if you applied. Search records: 180 days. Your account: until you delete it."],
  ];
  main.append(h("h1", {}, "Privacy & your data"), header,
    h("section", { class: "section mt6" }, h("h2", {}, "What runs where"),
      h("div", { class: "mt4" }, trustStrip(true)),
      h("details", { open: true }, h("summary", {}, "Row by row"),
        h("table", { class: "kv-table" }, h("tbody", {}, rows.map(([k, v]) => h("tr", {}, h("th", { scope: "row" }, k), h("td", {}, v))))))));
  if (!signedIn) return;
  const agreed = (kind) => state.me.consents?.[kind];
  main.append(
    h("section", { class: "section" }, h("h2", {}, "What you agreed to"),
      h("dl", { class: "agreed mt3" },
        [["privacy-notice", `Privacy notice (version ${meta.noticeVersion})`], ["terms", "Terms of use"], ["age-16-or-over", "Age 16 or over"]].map(([k, label]) =>
          h("div", {}, agreed(k) ? icon("check") : h("span", { class: "dot hollow", "aria-hidden": "true" }), h("dt", {}, label), h("dd", {}, agreed(k) ? "Agreed" : "Not given"))))),
    h("section", { class: "section" }, h("h2", {}, "Optional"),
      h("p", { class: "prose mt2" }, h("strong", { class: "lead" }, "Cover letters worded by the model on this server. "),
        agreed("model-wording") ? "On. " : "Off. Turn it on under Designer → Draft materials → Edit drafting."),
      agreed("model-wording") ? h("div", { class: "actions mt2" }, h("button", { class: "btn small", onclick: () => guard(async () => { await api.post("/api/me/consent/withdraw", { kinds: ["model-wording"] }); state.me = await api.get("/api/me"); await route(); }) }, "Withdraw")) : null),
    h("section", { class: "section" }, h("h2", {}, "Take your data with you"),
      h("div", { class: "actions" }, h("a", { class: "btn", href: "/api/me/export", download: "", "data-test": "export" }, "Download everything (JSON)")),
      h("p", { class: "hint mt3" }, "One file with every row about you: résumé versions, profile, preferences, pipeline, tracker, searches and your agreements. Access tokens are listed by reference and never exported."),
      h("p", { class: "hint" }, "Files the studio created in your Google Drive are already yours; they are not in this file. ", h("code", {}, "format jobsearch-studio-export/1"))),
    dangerZone());
}
function dangerZone() {
  const confirm = h("input", { type: "text", id: "delete-confirm", placeholder: "DELETE", "data-test": "delete-confirm", autocomplete: "off" });
  return h("section", { class: "section danger-zone" },
    h("h2", {}, "Delete your account"),
    h("ul", {},
      h("li", {}, h("strong", { class: "lead" }, "Erased now: "), "your account, résumé, profile, preferences, pipeline, tracker, searches and private steps."),
      h("li", {}, h("strong", { class: "lead" }, "Stays, and is yours: "), "files the studio created in your Google Drive."),
      h("li", {}, h("strong", { class: "lead" }, "Stays, with no link to you: "), "steps you shared to the library.")),
    field("Type DELETE to confirm", confirm),
    h("div", { class: "row" }, h("button", { class: "btn danger", "data-test": "delete-account", onclick: () => guard(async () => {
      await api.post("/api/me/delete", { confirm: confirm.value });
      // Fresh server state before leaving the page: changing the hash re-renders at once, and a sign-in page drawn
      // from the old metadata would still offer the deleted account.
      forgetDrafts(); state.me = null; state.meta = await api.get("/api/meta");
      location.hash = ""; toast("Your account has been deleted."); await boot();
    }) }, "Delete my account")),
    h("p", { class: "hint mt3" }, "You will be signed out. This cannot be undone."));
}

boot().catch((e) => { clear($("#main")).append(h("p", { class: "error-line" }, "The studio could not start: " + e.message)); });
