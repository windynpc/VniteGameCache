// @AutoLoad
// VGC MTool Bridge v1.0.0
// Optional, non-blocking capability check. Does not call @Eval*, does not modify game files, MTool data, Mods, or gameLib.
(async () => {
  const response = {
    protocolVersion: 1,
    bridgeVersion: "1.0.0",
    capabilities: [],
    gameCwd: null,
    error: null
  };

  try {
    if (typeof getGameCWD === "function") {
      response.capabilities.push("getGameCWD");
      response.gameCwd = await getGameCWD();
    }
  } catch (error) {
    response.error = String(error);
  }

  try {
    if (typeof console !== "undefined" && typeof console.log === "function") {
      console.log("[VGC MTool Bridge]", response);
    }
  } catch (_) {}
})();
