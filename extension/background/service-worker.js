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

function findElementsOnPage(request) {
  const nameNeedle = String(request?.name || "").trim().toLowerCase();
  const roleNeedle = String(request?.role || "").trim().toLowerCase();
  if (!nameNeedle) {
    return { ok: false, error: "name is required", matches: [] };
  }

  const roleMatches = {
    button: new Set(["button", "submit", "reset"]),
    link: new Set(["link", "a"]),
    textbox: new Set(["textbox", "text", "searchbox"]),
    menuitem: new Set(["menuitem"]),
    tab: new Set(["tab"]),
    checkbox: new Set(["checkbox"]),
    radio: new Set(["radio"]),
    icon: new Set(["img", "icon"])
  };
  const elements = Array.from(document.querySelectorAll(
    "button,a,input,select,textarea,img,[role],[aria-label],[title]"));
  const matches = [];
  for (const element of elements) {
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0 ||
        rect.width <= 0 || rect.height <= 0) {
      continue;
    }
    if ("disabled" in element && element.disabled) {
      continue;
    }

    const tag = element.tagName.toLowerCase();
    const explicitRole = (element.getAttribute("role") || "").trim().toLowerCase();
    const nativeRole = explicitRole || (tag === "a" ? "link" : tag === "button" ? "button" :
      tag === "input" || tag === "textarea" ? "textbox" : tag);
    if (roleNeedle) {
      const accepted = roleMatches[roleNeedle] || new Set([roleNeedle]);
      if (!accepted.has(nativeRole) && !accepted.has(tag)) {
        continue;
      }
    }

    const name = (element.getAttribute("aria-label") || element.getAttribute("title") ||
      element.getAttribute("alt") || element.getAttribute("placeholder") || element.textContent || "")
      .replace(/\s+/g, " ").trim();
    if (!name.toLowerCase().includes(nameNeedle)) {
      continue;
    }

    const scale = window.devicePixelRatio || 1;
    matches.push({
      name,
      role: nativeRole,
      bounds: {
        x: Math.round((window.screenX + (window.outerWidth - window.innerWidth) + rect.left) * scale),
        y: Math.round((window.screenY + (window.outerHeight - window.innerHeight) + rect.top) * scale),
        width: Math.round(rect.width * scale),
        height: Math.round(rect.height * scale)
      }
    });
    if (matches.length >= 8) {
      break;
    }
  }

  return { ok: true, matches };
}

async function handleToolCall(message) {
  const requestId = message.requestId;
  const tool = message.payload && message.payload.tool;
  if (tool === "find_element") {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab || tab.id === undefined) {
        return { type: "tool_result", requestId, payload: { ok: false, error: "No active browser tab was found." } };
      }

      const url = tab.url || "";
      if (/^(chrome|edge|about|view-source|chrome-extension):\/\//i.test(url)) {
        return { type: "tool_result", requestId, payload: { ok: false, error: "The active page is not accessible to the extension." } };
      }

      const args = message.payload?.args || {};
      const results = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        func: findElementsOnPage,
        args: [args]
      });
      return { type: "tool_result", requestId, payload: results[0]?.result || { ok: true, matches: [] } };
    } catch (error) {
      return {
        type: "tool_result",
        requestId,
        payload: { ok: false, error: `Unable to find the page element: ${error.message}` }
      };
    }
  }

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
