let nativePort = null;

chrome.action.onClicked.addListener(() => {
  if (nativePort) {
    nativePort.disconnect();
    nativePort = null;
  }

  try {
    nativePort = chrome.runtime.connectNative("com.geminiliveshare.proxy");
    console.log("GeminiLiveShare native messaging connected.");

    nativePort.onMessage.addListener((message) => {
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

    const testMessage = {
      type: "phase6a-echo-test",
      message: "hello from GeminiLiveShare extension"
    };
    nativePort.postMessage(testMessage);
    console.log("GeminiLiveShare native messaging test sent:", testMessage);
  } catch (error) {
    console.error("GeminiLiveShare native messaging connection error:", error);
    nativePort = null;
  }
});
