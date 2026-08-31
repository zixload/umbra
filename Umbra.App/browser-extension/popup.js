const INSTALL_URL = "https://github.com/zixload/umbra/releases/latest";
let sessionDeadline = 0;
let stopConfirmationUntil = 0;

document.getElementById("install").addEventListener("click", () => {
  chrome.tabs.create({ url: INSTALL_URL });
});

chrome.runtime.sendMessage({ action: "getConnectionStatus" }, response => {
  renderStatus(response);
});

document.getElementById("stop-session").addEventListener("click", () => {
  const button = document.getElementById("stop-session");
  if (Date.now() > stopConfirmationUntil) {
    stopConfirmationUntil = Date.now() + 3000;
    button.textContent = "Click again to stop";
    return;
  }

  button.disabled = true;
  chrome.runtime.sendMessage({ action: "stopSession" }, response => {
    stopConfirmationUntil = 0;
    if (chrome.runtime.lastError || !response?.ok) {
      showSessionError(response?.error === "hardMode"
        ? "This session is locked by Hard mode."
        : "Umbra could not stop the session.");
      button.disabled = response?.error === "hardMode";
      button.textContent = response?.error === "hardMode" ? "Locked by Hard mode" : "Stop session";
      return;
    }
    renderStatus({ connected: true, session: response.session });
  });
});

function renderStatus(response) {
  if (chrome.runtime.lastError || !response?.connected) return;
  document.getElementById("not-connected").hidden = true;
  document.getElementById("connected").hidden = false;

  const session = response.session;
  const active = Boolean(session?.active);
  document.getElementById("idle-message").hidden = active;
  document.getElementById("active-session").hidden = !active;
  if (!active) return;

  sessionDeadline = Number(session.endTs) || Date.now() + Number(session.remainingSeconds || 0) * 1000;
  const isPomodoro = session.kind === "pomodoro";
  const isBreak = isPomodoro && session.phase === "break";
  document.getElementById("session-phase").textContent = isBreak
    ? "Pomodoro · Break"
    : isPomodoro ? "Pomodoro · Focus" : "Focus session";
  document.getElementById("session-cycle").textContent = isPomodoro && session.cyclesTotal > 0
    ? `${session.cycle}/${session.cyclesTotal}`
    : "";
  document.getElementById("session-name").textContent = session.questName || "Focus session";

  const button = document.getElementById("stop-session");
  button.disabled = !session.canStop;
  button.textContent = !session.canStop
    ? "Locked by Hard mode"
    : Date.now() < stopConfirmationUntil ? "Click again to stop" : "Stop session";
  document.getElementById("session-error").hidden = true;
  updateCountdown();
}

function updateCountdown() {
  const seconds = Math.max(0, Math.ceil((sessionDeadline - Date.now()) / 1000));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const remainder = seconds % 60;
  document.getElementById("session-time").textContent = hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`
    : `${String(minutes).padStart(2, "0")}:${String(remainder).padStart(2, "0")}`;
}

function showSessionError(message) {
  const error = document.getElementById("session-error");
  error.textContent = message;
  error.hidden = false;
}

setInterval(() => {
  updateCountdown();
  chrome.runtime.sendMessage({ action: "getConnectionStatus" }, renderStatus);
}, 1000);
