const HOST = "com.umbra.browser_blocker";
let port;
let nativeConnected = false;
let activeSites = [];
let currentSession = null;
let reconnectTimer;
const lastAttempts = new Map();
const pendingRequests = new Map();

function connect() {
  clearTimeout(reconnectTimer);
  try {
    port = chrome.runtime.connectNative(HOST);
    port.onMessage.addListener(handleMessage);
    port.onDisconnect.addListener(() => {
      port = null;
      nativeConnected = false;
      currentSession = null;
      for (const pending of pendingRequests.values()) {
        clearTimeout(pending.timeout);
        pending.sendResponse({ ok: false, error: "notConnected" });
      }
      pendingRequests.clear();
      applyState(false, []);
      reconnectTimer = setTimeout(connect, 3000);
    });
    requestState();
  } catch {
    reconnectTimer = setTimeout(connect, 3000);
  }
}

function requestState() {
  if (!port) return;
  port.postMessage({ action: "getState" });
  setTimeout(requestState, 2000);
}

function handleMessage(message) {
  if (message?.requestId && pendingRequests.has(message.requestId)) {
    const pending = pendingRequests.get(message.requestId);
    clearTimeout(pending.timeout);
    pendingRequests.delete(message.requestId);
    pending.sendResponse(message);
  }
  if (message?.ok && typeof message.blocking === "boolean") {
    nativeConnected = true;
    currentSession = message.session || null;
    applyState(message.blocking, message.sites || []);
  }
}

async function applyState(blocking, sites) {
  // Une pause pomodoro (ou une session qui se termine) lève le blocage sans
  // qu'on l'ait demandé depuis cet onglet - sans ce suivi, un onglet vidéo
  // YouTube redirigé vers blocked.html au début du blocage restait bloqué
  // pour de bon une fois la pause revenue, alors que le blocage lui-même
  // était bien retombé côté app.
  const wasBlocking = activeSites.length > 0;
  activeSites = blocking ? sites : [];
  const oldRules = await chrome.declarativeNetRequest.getDynamicRules();
  const addRules = activeSites.map((site, index) => ({
    id: index + 1,
    priority: 1,
    action: { type: "redirect", redirect: { extensionPath: "/blocked.html" } },
    condition: {
      regexFilter: `^https?://([^/]+\\.)?${escapeRegex(site)}(?::[0-9]+)?(?:/|$)`,
      resourceTypes: ["main_frame"]
    }
  }));
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: oldRules.map(rule => rule.id),
    addRules
  });
  if (blocking) {
    await redirectAlreadyOpenTabs();
  } else if (wasBlocking) {
    await restoreBlockedTabs();
  }
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function matchingSite(url) {
  let host;
  try { host = new URL(url).hostname.replace(/^www\./, ""); } catch { return null; }
  return activeSites.find(site => host === site || host.endsWith(`.${site}`)) || null;
}

async function redirectAlreadyOpenTabs() {
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs) {
    if (!tab.id || !tab.url || !matchingSite(tab.url)) continue;
    const target = chrome.runtime.getURL("blocked.html") + "?from=" + encodeURIComponent(tab.url);
    await chrome.tabs.update(tab.id, { url: target });
  }
}

// Symétrique de redirectAlreadyOpenTabs : ramène chaque onglet actuellement
// sur blocked.html à la page qu'il affichait avant d'être bloqué (retenue
// dans le paramètre ?from, pas en mémoire de ce service worker - un service
// worker MV3 peut être tué/relancé n'importe quand, la donnée doit survivre
// à ça). Un onglet arrivé sur blocked.html via une tentative de navigation
// pendant le blocage (pas via redirectAlreadyOpenTabs) n'a pas de ?from -
// rien à restaurer pour lui, on le laisse tel quel.
async function restoreBlockedTabs() {
  const prefix = chrome.runtime.getURL("blocked.html");
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs) {
    if (!tab.id || !tab.url || !tab.url.startsWith(prefix)) continue;
    const from = new URL(tab.url).searchParams.get("from");
    if (from) await chrome.tabs.update(tab.id, { url: from });
  }
}

chrome.webNavigation.onBeforeNavigate.addListener(details => {
  if (details.frameId !== 0 || !port) return;
  const matched = matchingSite(details.url);
  if (!matched) return;
  const now = Date.now();
  if (now - (lastAttempts.get(matched) || 0) < 3000) return;
  lastAttempts.set(matched, now);
  port.postMessage({ action: "recordAttempt", target: matched });
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.action === "getConnectionStatus") {
    // activeSites reflète déjà tout ce qui bloque réellement (session +
    // plages + AlwaysBlocklist confondus, voir BrowserBlockingState côté
    // app) - le popup peut donc savoir "ça bloque" même quand
    // BrowserSessionControl (session uniquement) ne voit aucune session.
    sendResponse({ connected: nativeConnected, session: currentSession, blocking: activeSites.length > 0, sites: activeSites });
    return;
  }
  if (message?.action !== "stopSession") return;
  if (!nativeConnected || !port) {
    sendResponse({ ok: false, error: "notConnected" });
    return;
  }

  const requestId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const timeout = setTimeout(() => {
    const pending = pendingRequests.get(requestId);
    if (!pending) return;
    pendingRequests.delete(requestId);
    pending.sendResponse({ ok: false, error: "timeout" });
  }, 5000);
  pendingRequests.set(requestId, { sendResponse, timeout });
  port.postMessage({ action: "stopSession", requestId });
  return true;
});

connect();
