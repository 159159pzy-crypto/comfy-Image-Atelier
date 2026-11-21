/* Keep one speculative original for the next artwork detail. */
const DetailImageIntent = (() => {
  const dwell = 110, lifetime = 30000;
  const hoverActions = new WeakMap();
  let candidate = null, tabFocus = false, tabTimer, mouseX, mouseY;

  // Layout, scrolling and dialog dismissal can put a card under a stationary
  // pointer. Compare viewport coordinates across the document, including areas
  // outside the gallery. The first real move can report zero movementX/Y.
  function mouseMoved(event) {
    const moved = event.clientX !== mouseX || event.clientY !== mouseY;
    mouseX = event.clientX;
    mouseY = event.clientY;
    return moved;
  }
  document.addEventListener("pointerdown", event => { if (event.pointerType === "mouse") mouseMoved(event); }, true);
  document.addEventListener("pointermove", event => {
    if (event.pointerType === "mouse" && mouseMoved(event) && !event.buttons) {
      hoverActions.get(event.target.closest?.("button"))?.();
    }
  }, true);

  // Native dialog dismissal may focus its opener with :focus-visible as well.
  document.addEventListener("keydown", event => {
    if (event.key !== "Tab") return;
    tabFocus = true;
    clearTimeout(tabTimer);
    tabTimer = setTimeout(() => { tabFocus = false; }, 0);
  }, true);
  document.addEventListener("visibilitychange", () => { if (document.hidden) clear(); });

  function clear() {
    const entry = candidate;
    candidate = null;
    if (!entry) return;
    clearTimeout(entry.timer);
    entry.image?.removeAttribute("src");
  }

  function take(recordId) {
    const id = String(recordId);
    if (candidate?.id !== id) {
      clear();
      return null;
    }
    const entry = candidate;
    clearTimeout(entry.timer);
    candidate = null;
    // The detail image can join an in-flight request by setting the same URL.
    // Only a completed decode can supply pixels for the first frame.
    return entry.decoded ? entry.image : null;
  }

  function bind(button, recordId, shouldWarm = () => true) {
    const id = String(recordId);
    const eligible = () => button.isConnected && !document.hidden
      && !navigator.connection?.saveData && !["slow-2g", "2g"].includes(navigator.connection?.effectiveType) && shouldWarm();
    function request(immediate) {
      if (!immediate && candidate?.button === button) return;
      if (candidate?.button !== button) clear();
      if (!eligible()) { clear(); return; }
      const entry = candidate ||= { button, id, timer: null, image: null, decoded: false };
      if (entry.image) return;
      function start() {
        if (candidate !== entry) return;
        if (!eligible()) { clear(); return; }
        const image = entry.image = new Image();
        image.decoding = "async";
        image.src = `/api/images/${id}`;
        image.decode().then(() => { entry.decoded = true; }, () => { if (candidate === entry) clear(); });
        entry.timer = setTimeout(() => { if (candidate === entry) clear(); }, lifetime);
      }
      clearTimeout(entry.timer);
      if (immediate) start();
      else entry.timer = setTimeout(start, dwell);
    }
    const discard = () => { if (candidate?.button === button) clear(); };
    hoverActions.set(button, () => request(false));
    button.addEventListener("pointerleave", event => {
      const entry = candidate;
      if (entry?.button !== button) return;
      if (event.pointerType === "mouse") { clear(); return; }
      // Touch generates pointerleave on release before the synthetic click.
      clearTimeout(entry.timer);
      entry.timer = setTimeout(() => { if (candidate === entry) clear(); }, 350);
    });
    button.addEventListener("focus", () => {
      if (tabFocus && button.matches(":focus-visible")) request(false);
    });
    button.addEventListener("blur", discard);
    button.addEventListener("pointerdown", event => {
      if (event.isPrimary !== false && event.button === 0) request(true);
    });
    button.addEventListener("pointercancel", discard);
  }
  return { bind, take, clear };
})();
