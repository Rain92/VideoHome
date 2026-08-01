// Ambient backlight for the player: paints a tiny downscaled copy of the current
// video frame into a canvas sitting behind it, which CSS then blurs out past the
// edges. The colour spill tracks whatever is on screen.
//
// Deliberately standalone - no Blazor interop driving the loop. The page
// re-renders whenever the circuit pushes state, so anything holding element
// references would need re-wiring on every render; polling for them instead just
// cannot go stale. The component only calls in to read/write the on-off setting.
//
// Loaded without defer, so window.videoHome exists before the circuit starts and
// SyncVideo can read the stored preference during its initialisation.
(() => {
    'use strict';

    const FPS = 5;
    const STORAGE_KEY = 'videohome.ambient';

    // Off unless switched on: the backlight is an effect people should opt into, not
    // something that greets them on first load.
    //
    // Private browsing and blocked-storage setups throw on access rather than
    // returning null, so every touch of localStorage is guarded.
    function readStored() {
        try {
            return localStorage.getItem(STORAGE_KEY) === 'on';
        } catch {
            return false;
        }
    }

    let enabled = readStored();

    // Both sources are served from this origin (local files directly, YouTube
    // through the /youtube/{id} proxy), so drawImage never taints the canvas.
    // Still guarded: one throw a second with no handler would be miserable.
    let drawFailed = false;

    function tick() {
        const shell = document.querySelector('.video-shell');
        if (!shell) return;

        if (!enabled) {
            shell.classList.remove('is-lit');
            return;
        }

        const canvas = shell.querySelector('.video-ambient');
        const video = shell.querySelector('video');
        if (!canvas || !video) return;

        const hasFrames = video.readyState >= 2 && video.videoWidth > 0;
        const playing = hasFrames && !video.paused && !video.ended;

        if (!hasFrames || drawFailed) {
            shell.classList.remove('is-lit');
            return;
        }

        // While paused the last painted frame is still correct, so leave it be -
        // but do paint once when a new source first has pixels to show.
        if (!playing && shell.classList.contains('is-lit')) return;

        try {
            const ctx = canvas.__ambientCtx || (canvas.__ambientCtx = canvas.getContext('2d'));
            ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
            shell.classList.add('is-lit');
        } catch (e) {
            // SecurityError on a cross-origin source: give up for good rather than
            // retrying five times a second.
            drawFailed = true;
            shell.classList.remove('is-lit');
            console.warn('Ambient backlight disabled:', e);
        }
    }

    setInterval(tick, 1000 / FPS);

    // Merged rather than assigned: dashplayer.js hangs its own functions off the same
    // object, and whichever of the two loaded second would otherwise erase the first.
    window.videoHome = window.videoHome || {};
    Object.assign(window.videoHome, {
        getAmbient: () => enabled,

        setAmbient: on => {
            enabled = !!on;
            try {
                localStorage.setItem(STORAGE_KEY, enabled ? 'on' : 'off');
            } catch {
                // Preference just won't survive the reload; the toggle still works.
            }
            if (!enabled) {
                // Don't wait up to 200ms for the next tick to drop the glow.
                document.querySelector('.video-shell')?.classList.remove('is-lit');
            }
        },
    });
})();
