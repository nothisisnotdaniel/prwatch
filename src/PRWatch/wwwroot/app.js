"use strict";

// Fixed texts: never show server or exception text.
const Texts = {
  loading: "Loading…",
  running: "PRWatch is running",
  version: "Version ",
  unreachable: "Can't reach PRWatch.",
};

const VersionRoute = "/api/version";

function showStatus(text) {
  document.getElementById("home-status").textContent = text;
}

function showHome() {
  document.getElementById("top").hidden = false;
  document.getElementById("home").hidden = false;
}

// Asks the server for its version with the stored token. Without a token, or on 401, token.js shows the token form.
async function loadHome() {
  if (!hasToken()) {
    showTokenView(false);
    return;
  }
  showHome();
  showStatus(Texts.loading);
  let response;
  try {
    response = await authFetch(VersionRoute, { cache: "no-store" });
  } catch {
    if (!tokenViewShown()) {
      showStatus(Texts.unreachable);
    }
    return;
  }
  if (!response.ok) {
    showStatus(Texts.unreachable);
    return;
  }
  let version = "";
  try {
    const body = await response.json();
    version = typeof body.version === "string" ? body.version.slice(0, 64) : "";
  } catch {
    version = "";
  }
  showStatus(version ? Texts.running + " · " + Texts.version + version : Texts.running);
}

function registerServiceWorker() {
  if (!("serviceWorker" in navigator)) {
    return;
  }
  navigator.serviceWorker.register("/sw.js").catch(() => undefined);
}

onTokenSaved(loadHome);
registerServiceWorker();
loadHome();
