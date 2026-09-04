let nativePort = null;
const reconnectAlarmName = "geminiliveshare-native-reconnect";
chrome.alarms.create(reconnectAlarmName, { periodInMinutes: 0.5 });

function connectToNativeHost() {
  if (nativePort) {
    console.log("GeminiLiveShare native messaging is already connected.");
    return;
  }

  try {
    nativePort = chrome.runtime.connectNative("com.geminiliveshare.proxy");
    console.log("GeminiLiveShare native messaging connected.");

    nativePort.onMessage.addListener((message) => {
      if (message && message.type === "tool_call") {
        handleToolCall(message).then((result) => nativePort?.postMessage(result));
        return;
      }

      if (message && message.type === "event" && message.payload && message.payload.code === "app_not_running") {
        console.error("GeminiLiveShare app not connected:", message.payload.message);
        return;
      }

      console.log("GeminiLiveShare native messaging response received:", message);
    });

    nativePort.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError;
      console.log(
        "GeminiLiveShare native messaging disconnected.",
        error ? error.message : "No error reported."
      );
      nativePort = null;
    });
  } catch (error) {
    console.error("GeminiLiveShare native messaging connection error:", error);
    nativePort = null;
  }
}

async function getActivePage() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab) {
    return { ok: false, accessible: false, error: "No active browser tab was found." };
  }

  const url = tab.url || "";
  if (tab.status === "loading") {
    return {
      ok: false,
      accessible: false,
      state: "still_loading",
      url: url || null,
      title: tab.title || null,
      error: "The active page is still loading; try again when navigation is complete."
    };
  }

  if (!url || !tab.title) {
    return {
      ok: false,
      accessible: false,
      state: "no_page_data",
      url: url || null,
      title: tab.title || null,
      error: "The active tab has no readable page URL or title."
    };
  }

  const restricted = /^(chrome|edge|about|view-source|chrome-extension):\/\//i.test(url);
  if (restricted) {
    return {
      ok: false,
      accessible: false,
      url,
      title: tab.title || null,
      error: "The active page is not accessible to the extension."
    };
  }

  return { ok: true, accessible: true, url, title: tab.title || null };
}

async function handleToolCall(message) {
  const requestId = message.requestId;
  const tool = message.payload && message.payload.tool;
  if (tool === "get_form_fields") {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab || tab.id === undefined) {
        return { type: "tool_result", requestId, payload: { ok: false, error: "No active browser tab was found." } };
      }

      const url = tab.url || "";
      if (/^(chrome|edge|about|view-source|chrome-extension):\/\//i.test(url)) {
        return {
          type: "tool_result",
          requestId,
          payload: { ok: false, error: "The active page is not accessible to the extension." }
        };
      }

      if (tab.status === "loading") {
        return {
          type: "tool_result",
          requestId,
          payload: { ok: false, state: "still_loading", error: "The active page is still loading; try again when navigation is complete." }
        };
      }

      const results = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        files: ["content/field-scanner.js"]
      });
      return { type: "tool_result", requestId, payload: results[0]?.result || { url, title: tab.title || null, fields: [], notices: [] } };
    } catch (error) {
      return {
        type: "tool_result",
        requestId,
        payload: { ok: false, error: `Unable to scan form fields: ${error.message}` }
      };
    }
  }

  if (tool !== "get_active_page") {
    return {
      type: "tool_result",
      requestId,
      payload: { ok: false, error: `Unsupported browser tool: ${tool || "unknown"}` }
    };
  }

  try {
    return { type: "tool_result", requestId, payload: await getActivePage() };
  } catch (error) {
    return {
      type: "tool_result",
      requestId,
      payload: {
        ok: false,
        accessible: false,
        error: `Unable to inspect the active page: ${error.message}`
      }
    };
  }
}

chrome.action.onClicked.addListener(() => {
  connectToNativeHost();
  if (nativePort) {
    const testMessage = {
      type: "phase6a-echo-test",
      message: "hello from GeminiLiveShare extension"
    };
    nativePort.postMessage(testMessage);
    console.log("GeminiLiveShare native messaging test sent:", testMessage);
    nativePort.postMessage({
      type: "event",
      requestId: crypto.randomUUID(),
      payload: { code: "page_context_request", reason: "extension_icon_clicked" }
    });
  }
});

chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create(reconnectAlarmName, { periodInMinutes: 0.5 });
  connectToNativeHost();
});
chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create(reconnectAlarmName, { periodInMinutes: 0.5 });
  connectToNativeHost();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === reconnectAlarmName) {
    connectToNativeHost();
  }
});

connectToNativeHost();
