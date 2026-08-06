// @AutoLoad
// VGC MTool Bridge Probe v1.0.0
(async () => {
  const result = {
    bridgeProbeVersion: "1.0.0",
    protocolVersion: 1,
    getGameCWD: typeof getGameCWD === "function",
    fetch: typeof fetch === "function",
    WebSocket: typeof WebSocket === "function",
    XMLHttpRequest: typeof XMLHttpRequest !== "undefined",
    require: typeof require === "function",
    process: typeof process !== "undefined",
    localStorage: typeof localStorage !== "undefined",
    gameCwd: null,
    error: null
  };

  try {
    if (result.getGameCWD) result.gameCwd = await getGameCWD();
  } catch (error) {
    result.error = String(error);
  }

  try {
    if (typeof console !== "undefined" && typeof console.log === "function") {
      console.log("[VGC MTool Bridge Probe]", result);
    }
  } catch (_) {}
})();
