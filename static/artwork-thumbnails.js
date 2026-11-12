/* One queue for archive thumbnails. Detached, already-started images keep their
 * slot until completion: cancelling an <img> does not cancel server-side work. */
const ArtworkThumbnails = (() => {
  const limit = 4, margin = 400, entries = new Map();
  let active = 0, enabled = true, frame = 0, sequence = 0, initialized = false;
  let visibleObserver = null, nearbyObserver = null;

  function schedule() {
    if (!frame) frame = requestAnimationFrame(pump);
  }
  function initialize() {
    if (initialized) return;
    initialized = true;
    if (typeof IntersectionObserver !== "undefined") {
      const observe = field => changes => {
        for (const change of changes) {
          const entry = entries.get(change.target);
          if (entry) entry[field] = change.isIntersecting;
        }
        schedule();
      };
      visibleObserver = new IntersectionObserver(observe("visible"), { rootMargin: "0px" });
      nearbyObserver = new IntersectionObserver(observe("nearby"), { rootMargin: `${margin}px 0px` });
    } else {
      window.addEventListener("scroll", schedule, { passive: true, capture: true });
    }
    window.addEventListener("resize", schedule, { passive: true });
    document.addEventListener("visibilitychange", schedule);
  }
  function paint(entry) {
    const loading = entry.state === "queued" || entry.state === "loading";
    entry.article.dataset.thumbnailState = entry.state;
    entry.article.classList.toggle("is-loading", loading);
    entry.article.setAttribute("aria-busy", String(loading));
    entry.image.classList.toggle("image-loaded", entry.state === "loaded");
    entry.image.classList.toggle("image-failed", entry.state === "error");
    entry.failure.hidden = entry.state !== "error";
    entry.retry.hidden = entry.state !== "error";
    entry.retry.disabled = loading;
  }
  function revoke(entry) {
    if (entry.objectUrl) URL.revokeObjectURL(entry.objectUrl);
    entry.objectUrl = "";
  }
  function position(entry) {
    const article = entry.article;
    if (!article.isConnected || article.closest("[hidden], [inert]") || !article.getClientRects().length) return null;
    const rect = article.getBoundingClientRect(), height = window.innerHeight, width = window.innerWidth;
    const horizontal = rect.right > 0 && rect.left < width;
    const visible = horizontal && rect.bottom > 0 && rect.top < height && (!visibleObserver || entry.visible);
    article.classList.toggle("is-offscreen", !visible);
    if (!horizontal || rect.bottom <= -margin || rect.top >= height + margin || (nearbyObserver && !entry.nearby)) return null;
    return { entry, visible, distance: Math.max(0, -rect.bottom, rect.top - height) };
  }
  function pump() {
    frame = 0;
    if (!enabled || document.hidden) return;
    const queue = [];
    for (const entry of entries.values()) {
      const location = position(entry);
      if (entry.state === "queued" && location) queue.push(location);
    }
    queue.sort((a, b) => Number(b.visible) - Number(a.visible) || a.distance - b.distance
      || (a.entry.article.compareDocumentPosition(b.entry.article) & 4 ? -1 : 1));
    for (const item of queue) {
      if (active >= limit) break;
      start(item.entry, item.visible);
    }
  }
  function start(entry, visible) {
    const image = entry.image, task = { id: ++sequence, settled: false, decoding: false, objectUrl: "" };
    active++;
    entry.task = task;
    entry.state = "loading";
    revoke(entry);
    image.removeAttribute("src");
    image.loading = "eager";
    image.decoding = "async";
    image.fetchPriority = visible ? "high" : "low";
    paint(entry);
    const finish = success => {
      if (task.settled) return;
      task.settled = true;
      image.removeEventListener("load", loaded);
      image.removeEventListener("error", failed);
      active--;
      if (entry.task === task) entry.task = null;
      if (entry.disposed || !success) {
        if (task.objectUrl) URL.revokeObjectURL(task.objectUrl);
        image.removeAttribute("src");
      } else {
        entry.objectUrl = task.objectUrl;
      }
      if (!entry.disposed) {
        entry.state = success ? "loaded" : "error";
        paint(entry);
      }
      schedule();
    };
    const loaded = () => {
      if (task.settled || task.decoding) return;
      task.decoding = true;
      // decode() can reject after a load event; both paths share one settlement.
      Promise.resolve().then(() => image.decode?.()).then(() => finish(true), () => finish(false));
    };
    const failed = () => finish(false);
    const assign = source => {
      image.addEventListener("load", loaded);
      image.addEventListener("error", failed);
      image.src = source;
      Promise.resolve().then(() => {
        if (!task.settled && image.complete) {
          if (image.naturalWidth) loaded(); else failed();
        }
      });
    };
    if (!entry.reload) {
      assign(entry.url);
      return;
    }
    entry.reload = false;
    // A deliberate retry bypasses a cached failure without changing cache keys.
    // Keep this slot while fetch runs, even if its card leaves the current page.
    void (async () => {
      try {
        const response = await fetch(entry.url, { cache: "reload" });
        if (!response.ok) throw new Error(`Thumbnail HTTP ${response.status}`);
        const body = await response.blob();
        if (entry.disposed) { finish(false); return; }
        task.objectUrl = URL.createObjectURL(body);
        assign(task.objectUrl);
      } catch { finish(false); }
    })();
  }
  function attach(article, image, retry) {
    initialize();
    const entry = { article, image, retry, failure: article.querySelector(".image-failure"),
      url: image.dataset.thumbnailUrl, state: "queued", disposed: false, reload: false, objectUrl: "", task: null };
    entries.set(article, entry);
    retry.addEventListener("click", event => {
      event.stopPropagation();
      if (entry.disposed || entry.state !== "error") return;
      const restoreFocus = document.activeElement === retry;
      entry.reload = true;
      entry.state = "queued";
      paint(entry);
      if (restoreFocus) article.querySelector(".image-card-open")?.focus({ preventScroll: true });
      schedule();
    });
    paint(entry);
    visibleObserver?.observe(article);
    nearbyObserver?.observe(article);
    schedule();
  }
  function remove(article) {
    const entry = entries.get(article);
    if (!entry) return;
    entries.delete(article);
    entry.disposed = true;
    visibleObserver?.unobserve(article);
    nearbyObserver?.unobserve(article);
    if (!entry.task) {
      revoke(entry);
      entry.image.removeAttribute("src");
    }
    schedule();
  }
  return { attach, remove, refresh: schedule, setActive(value) { enabled = Boolean(value); schedule(); } };
})();
