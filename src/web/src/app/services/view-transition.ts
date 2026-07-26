/** Where a switch was clicked, so the change can start from it. */
export interface Origin { x: number; y: number; }

export function prefersReducedMotion(): boolean {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/**
 * Applies a change to how the app looks as a circle opening from the control that caused it: the
 * new state is clipped in over a still frame of the old one (View Transitions API), so the eye
 * follows one moving edge instead of every surface changing at once.
 *
 * Shared by the theme and colour-scheme switches, which are the same gesture from the reader's
 * side: colour changing under text that stays where it is. The language switch deliberately does
 * *not* use it — there the words themselves change, and an animation over text is a delay before
 * you can read it.
 *
 * That both callers only set an attribute on <html> is what keeps this simple: the new frame is
 * correct as soon as `apply` returns, with no re-render to wait for.
 *
 * Falls back to a brief colour crossfade where the API is missing, and to an instant swap for a
 * reader who has asked for less motion — the change still happens, it just doesn't travel.
 */
export function revealFrom(origin: Origin | undefined, apply: () => void): void {
  if (prefersReducedMotion()) {
    apply();
    return;
  }

  const doc = document as Document & {
    startViewTransition?: (update: () => void | Promise<void>) =>
      { ready: Promise<void>; finished: Promise<void> };
  };

  if (!doc.startViewTransition) {
    const root = document.documentElement;
    root.classList.add('theme-transition');
    apply();
    window.setTimeout(() => root.classList.remove('theme-transition'), 450);
    return;
  }

  // Every pill and button in the app carries `transition: 0.15s` — which is shorthand for
  // transition *all*, colours included. Left alone they interpolate to the new scheme on the live
  // DOM underneath, so the circle reveals content that is itself still fading and the edge smears
  // into exactly the crossfade the reveal was meant to replace. It shows up worst on a scheme
  // change, where the accented controls are most of what actually differs.
  const root = document.documentElement;
  root.classList.add('switching');

  // Nothing is awaited in here on purpose: the browser holds the page frozen on the old frame until
  // this callback resolves, so anything that hangs in it hangs the app with no error anywhere.
  const transition = doc.startViewTransition(() => apply());

  if (origin) {
    void transition.ready.then(() => animate(origin));
  }
  void transition.finished.finally(() => root.classList.remove('switching'));
}

// The circle has to reach the furthest corner from the click, or the old state is left sitting in
// a corner of the screen when the animation ends.
function animate(origin: Origin): void {
  const radius = Math.hypot(
    Math.max(origin.x, window.innerWidth - origin.x),
    Math.max(origin.y, window.innerHeight - origin.y),
  );
  document.documentElement.animate(
    {
      clipPath: [
        `circle(0px at ${origin.x}px ${origin.y}px)`,
        `circle(${radius}px at ${origin.x}px ${origin.y}px)`,
      ],
    },
    {
      // Both numbers are set by how long the *edge* stays legible, not by how long the animation
      // lasts. The obvious ease-out — cubic-bezier(0.22, 1, 0.36, 1), the one the reveal started
      // with — puts more than half the expansion in the first tenth of the time: measured frame by
      // frame, the circle had crossed most of the viewport within 70ms. A theme flip survives that
      // because near-black to near-white is unmistakable in a glimpse, but a scheme change is a
      // handful of accents moving hue, and the edge was gone before it registered. Cubic ease-out
      // over a longer run keeps a visible boundary travelling for a few hundred milliseconds.
      duration: 700,
      easing: 'cubic-bezier(0.33, 1, 0.68, 1)',
      pseudoElement: '::view-transition-new(root)',
    },
  );
}

/** The centre of whatever was clicked — where the reveal should start. */
export function originOf(event: Event): Origin | undefined {
  const target = event.currentTarget as HTMLElement | null;
  if (!target) return undefined;
  const box = target.getBoundingClientRect();
  return { x: box.left + box.width / 2, y: box.top + box.height / 2 };
}
