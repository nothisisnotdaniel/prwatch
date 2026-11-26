"use strict";

// PRWatch's values for the RoomKit scripts: a classic script, loaded first by index.html and by the service worker
// (importScripts), whose top-level consts the other scripts read as globals.

// Sign-in (token.js). Never change the key: stored tokens would be lost.
const TokenKey = "prwatch-token";

// The app's entries of push.js' PushTexts.
const PushAppTexts = {
  homeScreen: "To get notifications on iPhone, add PRWatch to the Home Screen and open it from there.",
  denied: "Notifications are blocked for PRWatch. Allow them in Settings, then check again.",
};

// Service worker (sw.js). Caches only the app shell. Bump the version whenever a shell file changes.
const CacheVersion = "v2";
const CacheName = "prwatch-shell-" + CacheVersion;

const Shell = [
  "/",
  "/index.html",
  "/app.css",
  "/prwatch.css",
  "/app.js",
  "/token.js",
  "/room.js",
  "/manifest.webmanifest",
  "/icons/apple-touch-icon.png",
  "/icons/icon-192.png",
  "/icons/icon-512.png",
];

// The app's entries of the service worker's PushTitles, its fallback title, notification tag and update body prefix.
const PushAppTitles = {
  updated: "PRWatch updated",
};
const PushFallbackTitle = "PRWatch";
const PushDefaultTag = "prwatch";
const PushUpdatePrefix = "PRWatch ";
