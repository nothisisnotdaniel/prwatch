"use strict";

// PRWatch's page: the status bar, the on/off switch, the open PRs per project with their taps, and the settings.
// Every message is a fixed text from the maps below: never server, PR or exception text. PR text is set through
// textContent only.

const StatusRoute = "/api/status";
const PrsRoute = "/api/prs";
const SettingsRoute = "/api/settings";
const ReviewRoute = "/api/prs/review";
const MergeRoute = "/api/prs/merge";
const CancelRoute = "/api/prs/cancel";

// Status and PRs reload this often while the page is visible.
const RefreshSeconds = 30;

// An armed Consent or Merge button stays armed this long after the first tap; a refresh does not reset it.
const ArmSeconds = 5;

const PullUrl = /^https:\/\/github\.com\/[^/]+\/[^/]+\/pull\/[0-9]+$/;
const Sha = /^[0-9a-f]{40}$/;
const MaxTitle = 200;
const MaxAuthor = 100;
const ShortSha = 7;

const Texts = {
  loading: "Loading…",
  unreachable: "Can't reach PRWatch.",
  noPrs: "No open pull requests.",
  brakeOff: "Brake: off, reviews can start.",
  brakeHolding: "Brake holding: ",
  until: " until ",
  login: "Claude login: ",
  expiresSoon: "The Claude login expires soon: ",
  noPoll: "No poll yet.",
  lastPoll: "Last poll: ",
  noError: "No errors.",
  lastError: "Last error: ",
  skipped: "Skipped: ",
  trusted: "trusted",
  untrusted: "not trusted",
  noRound: "Not reviewed yet",
  round: "Round ",
  clean: "clean",
  notClean: "not clean",
  current: "current",
  outdated: "outdated",
  tests: "tests ",
  testsUnknown: "tests not run",
  mergeable: "mergeable",
  notMergeable: "not mergeable",
  mergeableUnknown: "mergeable unknown",
  lastRun: "Last run ",
  running: "Running since ",
  consent: "Consent",
  merge: "Merge",
  cancel: "Cancel",
  confirmConsent: "Tap again to consent to ",
  confirmMerge: "Tap again to merge ",
  by: "by ",
  saved: "Saved.",
  noChanges: "Nothing changed.",
  defaultMode: "Default mode",
  effectiveMode: "Reviews run in mode ",
};

// The fixed texts of the request routes' error codes (POST /api/prs/review, merge, cancel).
const RequestErrors = {
  invalid_request: "PRWatch could not take this request. Check the factory root in the settings.",
  unknown_pr: "This pull request is no longer open. The list has been reloaded.",
  stale_request: "The pull request got a new push. Check the new head, then tap again.",
  write_failed: "PRWatch could not save the request. Try again.",
};

// The fixed texts of the settings route's error codes.
const SettingsErrors = {
  invalid_settings: "Some settings are not valid. See the marked fields.",
  save_failed: "PRWatch could not save the settings. Try again.",
};

// The fixed text for each field invalid_settings can name.
const SettingsFieldErrors = {
  enabled: "Watching needs a factory root and a claude path in the settings.",
  factoryRoot: "Enter the absolute path of a folder with factory/tools/job.py and factory/modes.json.",
  projects: "Choose projects the factory knows, or none for every project.",
  pollMinutes: "Enter whole minutes from 1 to 60.",
  quietMinutes: "Enter whole minutes from 0 to 120.",
  autoReviewTrusted: "Turn this on or off.",
  autoMergeTrusted: "Turn this on or off.",
  reviewerMode: "Choose a mode from factory/modes.json.",
  maxBudgetUsd: "Enter an amount from 0.1 to 20.",
  brakeFiveHourPercent: "Enter a percentage from 1 to 100.",
  brakeSevenDayPercent: "Enter a percentage from 1 to 100.",
  brakeFiveHourUsd: "Enter an amount from 0.5 to 500.",
  brakeSevenDayUsd: "Enter an amount from 0.5 to 500.",
  brakeRunsPerHour: "Enter a whole number from 1 to 10.",
  claudePath: "Enter the absolute path of an executable claude program.",
  _unknown: "PRWatch does not know one of the settings. Reload the page.",
};

// Shown next to the factory root when a change of another field fails because the stored root is invalid.
const FactoryRootFirst = "Fix the factory root first: the stored one is not valid, so no other setting can be saved.";

const NetworkError = "Can't reach PRWatch. Check the connection and try again.";
const ErrorFallback = "Something went wrong. Try again.";

// The fixed texts of the status file's lastError codes.
const StatusErrors = {
  settings_unreadable: "The settings file can't be read, so the defaults are in use and watching is off. Save the settings below to replace it.",
  status_write_failed: "PRWatch could not save its status file.",
  python_missing: "python3 was not found, so PRWatch can't poll.",
  poll_timeout: "The last poll took too long and was stopped.",
  poll_failed: "The last poll failed.",
  poll_output_invalid: "The last poll printed something PRWatch can't read.",
  claude_not_found: "claude not found. Check the claude path in the settings.",
  model_unknown: "The reviewer mode has no model. Check the reviewer mode in the settings.",
  run_failed: "The last review run could not start.",
  ledger_write_failed: "PRWatch could not save its usage ledger.",
};

// Why the token brake holds.
const BrakeReasons = {
  rate_limited: "Claude's rate limit was reached",
  five_hour_usage: "the 5-hour plan usage passed its setting",
  seven_day_usage: "the weekly plan usage passed its setting",
  five_hour_cost: "the 5-hour cost passed its setting",
  seven_day_cost: "the 7-day cost passed its setting",
  runs_per_hour: "the runs per hour reached their setting",
};

const LoginStates = {
  unknown: "not checked yet",
  ok: "signed in",
  needs_login: "needs login. Run claude on the Mac and sign in, then save the settings.",
};

const SkipReasons = {
  gh_missing: "gh is missing",
  no_remote: "no GitHub remote",
  gh_failed: "gh failed",
  unknown: "not a known project",
  job_active: "a factory job is running",
};

const RunResults = {
  ok: "finished",
  error: "ended with an error",
  budget: "stopped at its budget",
  timeout: "took too long and was stopped",
  output_too_large: "printed too much and was stopped",
  auth: "needs a Claude login",
  stopped: "stopped when PRWatch stopped",
};

const Stages = {
  review: "in review",
  merged: "merged",
  closed: "closed",
};

const TestResults = {
  pass: "pass",
  fail: "fail",
  none: "none",
};

const Requests = {
  review: "Review requested, waiting for the next poll.",
  merge: "Merge requested, waiting for the next poll.",
  cancel: "Cancel requested, waiting for the next poll.",
};

// Shown for a request the last run already used (requestUnused): only a new tap sends it again.
const RequestUnused = "The last run did not use this request. Tap again to send it once more.";

const Fallback = "unknown";

const NumberFields = [
  "pollMinutes", "quietMinutes", "maxBudgetUsd", "brakeFiveHourPercent", "brakeSevenDayPercent",
  "brakeFiveHourUsd", "brakeSevenDayUsd", "brakeRunsPerHour",
];
const CheckFields = ["autoReviewTrusted", "autoMergeTrusted"];
const TextFields = ["factoryRoot", "claudePath"];

let refreshTimer = null;
// The one armed Consent or Merge button: { project, number, action, headSha, until }, its button as last drawn,
// and the timer that disarms it.
let armed = null;
let armedButton = null;
let armTimer = null;
let loadedSettings = null;

// A fixed text from map for code, else fallback; never the code itself.
function textFor(map, code, fallback) {
  return typeof code === "string" && Object.prototype.hasOwnProperty.call(map, code) ? map[code] : fallback;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
}

function formatTime(value) {
  if (typeof value !== "string") {
    return "";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "";
  }
  return date.toLocaleString([], { dateStyle: "short", timeStyle: "short" });
}

function capped(value, max) {
  return typeof value === "string" ? value.slice(0, max) : "";
}

function showMessage(id, text) {
  const node = document.getElementById(id);
  node.textContent = text;
  node.hidden = text === "";
}

function showHome() {
  document.getElementById("top").hidden = false;
  document.getElementById("home").hidden = false;
}

async function readJson(response) {
  try {
    return await response.json();
  } catch {
    return null;
  }
}

// --- Status bar ---

function renderStatus(status) {
  const brake = status && status.brake;
  if (brake && brake.holding === true) {
    const until = formatTime(brake.until);
    showMessage("status-brake",
      Texts.brakeHolding + textFor(BrakeReasons, brake.reason, Fallback) + (until ? Texts.until + until : ""));
  } else {
    showMessage("status-brake", Texts.brakeOff);
  }

  const login = (status && status.login) || {};
  let loginText = Texts.login + textFor(LoginStates, login.state, Fallback);
  if (login.expiresSoon === true) {
    loginText += " " + Texts.expiresSoon + formatTime(login.credentialExpiresAt);
  }
  showMessage("status-login", loginText);
  document.getElementById("status-login").classList.toggle("warn",
    login.state === "needs_login" || login.expiresSoon === true);

  const poll = formatTime(status && status.lastPoll);
  showMessage("status-poll", poll ? Texts.lastPoll + poll : Texts.noPoll);

  const error = status && status.lastError;
  const errorLine = document.getElementById("status-error");
  if (error && typeof error.code === "string") {
    const at = formatTime(error.at);
    showMessage("status-error",
      Texts.lastError + textFor(StatusErrors, error.code, ErrorFallback) + (at ? " (" + at + ")" : ""));
    errorLine.classList.add("warn");
  } else {
    showMessage("status-error", Texts.noError);
    errorLine.classList.remove("warn");
  }

  const skipped = document.getElementById("status-skipped");
  skipped.replaceChildren();
  for (const skip of Array.isArray(status && status.skipped) ? status.skipped : []) {
    skipped.append(element("li", "", Texts.skipped + capped(skip.project, 64) + ": " +
      textFor(SkipReasons, skip.reason, Fallback)));
  }
}

async function loadStatus() {
  let response;
  try {
    response = await authFetch(StatusRoute, { cache: "no-store" });
  } catch {
    return false;
  }
  if (!response.ok) {
    return false;
  }
  renderStatus(await readJson(response));
  return true;
}

// --- PR list ---

function shortSha(sha) {
  return sha.slice(0, ShortSha);
}

function roundText(round) {
  if (!round) {
    return Texts.noRound;
  }
  const parts = [
    Texts.round + String(round.k),
    round.clean === true ? Texts.clean : Texts.notClean,
    round.current === true ? Texts.current : Texts.outdated,
    round.tests === null || round.tests === undefined
      ? Texts.testsUnknown
      : Texts.tests + textFor(TestResults, round.tests, Fallback),
  ];
  if (round.mergeable === true) {
    parts.push(Texts.mergeable);
  } else if (round.mergeable === false) {
    parts.push(Texts.notMergeable);
  } else {
    parts.push(Texts.mergeableUnknown);
  }
  return parts.join(" · ");
}

function runText(run) {
  if (!run) {
    return "";
  }
  if (!run.endedAt) {
    return Texts.running + formatTime(run.startedAt);
  }
  const parts = [Texts.lastRun + formatTime(run.endedAt), textFor(RunResults, run.result, Fallback)];
  if (typeof run.costUsd === "number" && Number.isFinite(run.costUsd)) {
    parts.push("$" + run.costUsd.toFixed(2));
  }
  return parts.join(" · ");
}

// Posts a tap to its route: only the rendered row's project, number and head (Consent adds consent: true).
function sendRequest(action, body) {
  const options = {
    method: "POST",
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  };
  if (action === "merge") {
    return authFetch(MergeRoute, options);
  }
  if (action === "cancel") {
    return authFetch(CancelRoute, options);
  }
  return authFetch(ReviewRoute, options);
}

async function tap(action, pr) {
  showMessage("request-error", "");
  const body = { project: pr.project, number: pr.number, headSha: pr.headSha };
  if (action === "consent") {
    body.consent = true;
  }
  let response;
  try {
    response = await sendRequest(action, body);
  } catch {
    if (!tokenViewShown()) {
      showMessage("request-error", NetworkError);
    }
    return;
  }
  if (!response.ok) {
    const error = await readJson(response);
    showMessage("request-error", textFor(RequestErrors, error && error.error, ErrorFallback));
  }
  await refresh();
}

function actionLabel(action) {
  return action === "consent" ? Texts.consent : action === "merge" ? Texts.merge : Texts.cancel;
}

function armedForPr(action, pr) {
  return armed !== null && armed.project === pr.project && armed.number === pr.number && armed.action === action;
}

function armedFor(action, pr) {
  return armedForPr(action, pr) && armed.headSha === pr.headSha && Date.now() < armed.until;
}

// Ends the armed window: clears the state and the timer, and redraws the button unarmed if it is still shown.
function disarm() {
  if (armTimer !== null) {
    clearTimeout(armTimer);
    armTimer = null;
  }
  if (armed !== null && armedButton !== null && armedButton.isConnected) {
    const label = actionLabel(armed.action);
    const head = shortSha(armed.headSha);
    armedButton.classList.remove("armed");
    delete armedButton.dataset.armed;
    armedButton.textContent = label + " · " + head;
  }
  armed = null;
  armedButton = null;
}

function actionButton(action, pr) {
  const head = shortSha(pr.headSha);
  const label = actionLabel(action);
  const button = element("button", "action action-" + action, label + " · " + head);
  button.type = "button";
  const confirm = action === "consent" ? Texts.confirmConsent : action === "merge" ? Texts.confirmMerge : "";
  if (confirm && armedFor(action, pr)) {
    // A refresh inside the armed window draws the button armed again.
    button.dataset.armed = "yes";
    button.classList.add("armed");
    button.textContent = confirm + head;
    armedButton = button;
  } else if (confirm && armedForPr(action, pr)) {
    // A new head (or an ended window): the armed state never carries over.
    disarm();
  }
  button.addEventListener("click", () => {
    // Cancel sends on one tap.
    if (confirm === "") {
      button.disabled = true;
      tap(action, pr);
      return;
    }
    // Consent and Merge need a second tap that names the head, within ArmSeconds of the first.
    if (!armedFor(action, pr)) {
      disarm();
      armed = { project: pr.project, number: pr.number, action: action, headSha: pr.headSha,
        until: Date.now() + ArmSeconds * 1000 };
      armedButton = button;
      button.dataset.armed = "yes";
      button.classList.add("armed");
      button.textContent = confirm + head;
      armTimer = setTimeout(disarm, ArmSeconds * 1000);
      return;
    }
    armed = null;
    armedButton = null;
    clearTimeout(armTimer);
    armTimer = null;
    button.disabled = true;
    tap(action, pr);
  });
  return button;
}

function prRow(pr) {
  const row = element("li", "pr");
  const head = element("p", "pr-head");
  const number = "#" + String(pr.number);
  const title = capped(pr.title, MaxTitle);
  if (typeof pr.url === "string" && PullUrl.test(pr.url) && pr.url.endsWith("/pull/" + String(pr.number))) {
    const link = element("a", "pr-link", number + " " + title);
    link.href = pr.url;
    link.rel = "noopener noreferrer";
    link.target = "_blank";
    head.append(link);
  } else {
    head.append(element("span", "pr-link", number + " " + title));
  }
  row.append(head);

  const meta = element("p", "pr-meta");
  meta.append(element("span", "", Texts.by + capped(pr.author, MaxAuthor)));
  meta.append(element("span", pr.trusted === true ? "chip chip-trusted" : "chip chip-untrusted",
    pr.trusted === true ? Texts.trusted : Texts.untrusted));
  if (pr.stage) {
    meta.append(element("span", "chip chip-stage", textFor(Stages, pr.stage, Fallback)));
  }
  row.append(meta);

  const round = element("p", "pr-round", roundText(pr.round));
  if (pr.round && pr.round.clean === true && pr.round.current === true) {
    round.classList.add("good");
  }
  row.append(round);

  const run = runText(pr.run);
  if (run) {
    row.append(element("p", "pr-run", run));
  }
  if (pr.request && pr.requestUnused === true) {
    row.append(element("p", "pr-request", RequestUnused));
  } else if (pr.request) {
    row.append(element("p", "pr-request", textFor(Requests, pr.request, Fallback)));
  }

  const actions = Array.isArray(pr.actions) ? pr.actions : [];
  if (actions.length > 0) {
    const bar = element("div", "pr-actions");
    for (const action of ["consent", "merge", "cancel"]) {
      if (actions.includes(action)) {
        bar.append(actionButton(action, pr));
      }
    }
    row.append(bar);
  }
  return row;
}

function renderPrs(body) {
  const list = document.getElementById("prs");
  list.replaceChildren();
  const prs = (body && Array.isArray(body.prs) ? body.prs : [])
    .filter(pr => pr && typeof pr.project === "string" && Number.isInteger(pr.number)
      && typeof pr.headSha === "string" && Sha.test(pr.headSha));
  if (prs.length === 0) {
    list.append(element("p", "empty", Texts.noPrs));
    return;
  }
  let group = null;
  let project = null;
  for (const pr of prs) {
    if (pr.project !== project) {
      project = pr.project;
      const section = element("section", "pr-project");
      section.append(element("h2", "", capped(project, 64)));
      group = element("ul", "pr-list");
      section.append(group);
      list.append(section);
    }
    group.append(prRow(pr));
  }
}

async function loadPrs() {
  let response;
  try {
    response = await authFetch(PrsRoute, { cache: "no-store" });
  } catch {
    return false;
  }
  if (!response.ok) {
    return false;
  }
  renderPrs(await readJson(response));
  return true;
}

// --- Settings ---

function field(name) {
  return document.getElementById("field-" + name);
}

function clearFieldErrors() {
  for (const name of Object.keys(SettingsFieldErrors)) {
    const node = document.getElementById("error-" + name);
    if (node) {
      showMessage("error-" + name, "");
    }
  }
  showMessage("settings-error", "");
  showMessage("switch-error", "");
}

function fillOptions(select, names, selected, emptyLabel) {
  select.replaceChildren();
  if (emptyLabel !== undefined) {
    const none = element("option", "", emptyLabel);
    none.value = "";
    select.append(none);
  }
  for (const name of Array.isArray(names) ? names : []) {
    if (typeof name !== "string") {
      continue;
    }
    const option = element("option", "", capped(name, 64));
    option.value = name;
    option.selected = Array.isArray(selected) ? selected.includes(name) : selected === name;
    select.append(option);
  }
}

function fillSettings(view) {
  loadedSettings = view;
  document.getElementById("enabled").checked = view.enabled === true;
  for (const name of TextFields) {
    field(name).value = typeof view[name] === "string" ? view[name] : "";
  }
  for (const name of NumberFields) {
    field(name).value = typeof view[name] === "number" ? String(view[name]) : "";
  }
  for (const name of CheckFields) {
    field(name).checked = view[name] === true;
  }
  fillOptions(field("projects"), view.knownProjects, view.projects);
  fillOptions(field("reviewerMode"), view.knownModes, view.reviewerMode, Texts.defaultMode);
  showMessage("effective-mode",
    typeof view.effectiveMode === "string" ? Texts.effectiveMode + capped(view.effectiveMode, 64) : "");
}

// The form's values, or null with the bad number fields marked.
function formValues() {
  const values = {};
  let valid = true;
  for (const name of TextFields) {
    const text = field(name).value.trim();
    values[name] = text === "" ? null : text;
  }
  for (const name of NumberFields) {
    const text = field(name).value.trim();
    const number = Number(text);
    if (text === "" || !Number.isFinite(number)) {
      showMessage("error-" + name, SettingsFieldErrors[name]);
      valid = false;
    }
    values[name] = number;
  }
  for (const name of CheckFields) {
    values[name] = field(name).checked;
  }
  const projects = Array.from(field("projects").selectedOptions, option => option.value);
  values.projects = projects.length === 0 ? null : projects;
  const mode = field("reviewerMode").value;
  values.reviewerMode = mode === "" ? null : mode;
  return valid ? values : null;
}

// Shows the fixed texts of a failed settings POST; changed holds the fields the POST sent.
async function showSettingsError(response, changed, messageId) {
  const error = await readJson(response);
  const code = error && error.error;
  const fields = error && Array.isArray(error.fields) ? error.fields : [];
  showMessage(messageId, textFor(SettingsErrors, code, ErrorFallback));
  if (code !== "invalid_settings") {
    return;
  }
  for (const name of fields) {
    if (name === "enabled" && messageId === "switch-error") {
      showMessage("switch-error", SettingsFieldErrors.enabled);
    } else if (name === "factoryRoot" && !Object.prototype.hasOwnProperty.call(changed, "factoryRoot")) {
      showMessage("error-factoryRoot", FactoryRootFirst);
    } else if (typeof name === "string" && Object.prototype.hasOwnProperty.call(SettingsFieldErrors, name)
      && document.getElementById("error-" + name)) {
      showMessage("error-" + name, SettingsFieldErrors[name]);
    }
  }
}

function postSettings(changes) {
  return authFetch(SettingsRoute, {
    method: "POST",
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(changes),
  });
}

async function saveSettings(event) {
  event.preventDefault();
  clearFieldErrors();
  showMessage("settings-status", "");
  if (loadedSettings === null) {
    showMessage("settings-error", NetworkError);
    return;
  }
  const values = formValues();
  if (values === null) {
    showMessage("settings-error", SettingsErrors.invalid_settings);
    return;
  }
  // Only the fields that changed are sent.
  const changes = {};
  for (const name of Object.keys(values)) {
    if (JSON.stringify(values[name]) !== JSON.stringify(loadedSettings[name] === undefined ? null : loadedSettings[name])) {
      changes[name] = values[name];
    }
  }
  if (Object.keys(changes).length === 0) {
    showMessage("settings-status", Texts.noChanges);
    return;
  }
  let response;
  try {
    response = await postSettings(changes);
  } catch {
    if (!tokenViewShown()) {
      showMessage("settings-error", NetworkError);
    }
    return;
  }
  if (!response.ok) {
    await showSettingsError(response, changes, "settings-error");
    return;
  }
  const view = await readJson(response);
  if (view) {
    fillSettings(view);
  }
  showMessage("settings-status", Texts.saved);
  await loadStatus();
}

// The switch posts only {"enabled": <bool>}; on an error it flips back.
async function toggleEnabled() {
  const box = document.getElementById("enabled");
  const enabled = box.checked;
  clearFieldErrors();
  box.disabled = true;
  let response;
  try {
    response = await postSettings({ enabled: enabled });
  } catch {
    box.checked = !enabled;
    box.disabled = false;
    if (!tokenViewShown()) {
      showMessage("switch-error", NetworkError);
    }
    return;
  }
  box.disabled = false;
  if (!response.ok) {
    box.checked = !enabled;
    await showSettingsError(response, { enabled: enabled }, "switch-error");
    return;
  }
  const view = await readJson(response);
  if (view) {
    fillSettings(view);
  }
  await loadStatus();
}

async function loadSettings() {
  let response;
  try {
    response = await authFetch(SettingsRoute, { cache: "no-store" });
  } catch {
    return;
  }
  if (response.ok) {
    const view = await readJson(response);
    if (view) {
      fillSettings(view);
    }
  }
}

// --- Loading and refresh ---

async function refresh() {
  if (!hasToken() || tokenViewShown()) {
    return;
  }
  const results = await Promise.all([loadStatus(), loadPrs()]);
  if (results.every(ok => ok)) {
    showMessage("home-status", "");
  } else if (!tokenViewShown()) {
    showMessage("home-status", Texts.unreachable);
  }
}

function stopRefresh() {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }
}

function startRefresh() {
  stopRefresh();
  if (document.visibilityState === "visible") {
    refreshTimer = setInterval(refresh, RefreshSeconds * 1000);
  }
}

// Without a token, or on 401, token.js shows the token form.
async function loadHome() {
  if (!hasToken()) {
    showTokenView(false);
    return;
  }
  showHome();
  showMessage("home-status", Texts.loading);
  await Promise.all([refresh(), loadSettings()]);
  startRefresh();
}

function onVisibilityChange() {
  if (document.visibilityState === "visible") {
    refresh();
    startRefresh();
  } else {
    stopRefresh();
    disarm();
  }
}

function registerServiceWorker() {
  if (!("serviceWorker" in navigator)) {
    return;
  }
  navigator.serviceWorker.register("/sw.js").catch(() => undefined);
}

document.getElementById("enabled").addEventListener("change", toggleEnabled);
document.getElementById("settings-form").addEventListener("submit", saveSettings);
document.addEventListener("visibilitychange", onVisibilityChange);
onTokenSaved(loadHome);
registerServiceWorker();
loadHome();
