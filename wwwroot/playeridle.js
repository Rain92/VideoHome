// Fades the skip-back overlay out along with the native control bar.
//
// CSS alone can only reveal the button on :hover, and :hover stays true for as long
// as the pointer sits inside the frame - so a mouse parked on the video left the
// button burning while the browser's own controls had long since faded. There is no
// event for "the native bar hid itself", so this reproduces its rule: idle for a
// couple of seconds and the overlay goes with it.
//
// Standalone and element-lookup-per-call for the same reason as ambient.js: Blazor
// re-renders on every circuit push, and a held reference can go stale. The class
// lands on .video-shell rather than .video-frame because the frame's class attribute
// is Blazor's (it writes is-paused into it) and a re-render would wipe ours out.
(() => {
    'use strict';

    // Chrome and Firefox both give the native bar about this long after the last
    // pointer movement. Matching it is the whole point.
    const IDLE_MS = 2000;

    let timer = null;

    function shell() {
        return document.querySelector('.video-shell');
    }

    function goIdle() {
        timer = null;

        // The pointer resting on the button itself is the one case where hiding is
        // wrong - that is a user aiming at it, not an idle screen. Same as the native
        // bar, which stays up while the mouse is over it.
        const button = document.querySelector('.player-skip-back');
        if (button && button.matches(':hover')) {
            timer = setTimeout(goIdle, IDLE_MS);
            return;
        }

        shell()?.classList.add('is-idle');
    }

    function wake() {
        shell()?.classList.remove('is-idle');
        clearTimeout(timer);
        timer = setTimeout(goIdle, IDLE_MS);
    }

    // Capture phase: the video element and the overlay button both sit inside the
    // frame, and neither is guaranteed to let a move bubble up untouched.
    document.addEventListener('pointermove', e => {
        if (e.target instanceof Element && e.target.closest('.video-frame'))
            wake();
    }, true);

    // Leaving the frame ends the hover anyway, so there is nothing left to hide -
    // but the stale timer would otherwise fire mid-way into the next hover and cut
    // it short. Clear it and start the next hover from a full countdown.
    document.addEventListener('pointerout', e => {
        if (!(e.target instanceof Element) || !e.target.closest('.video-frame'))
            return;
        // relatedTarget is where the pointer went; still inside the frame means this
        // was just a move between the video and the button.
        const to = e.relatedTarget;
        if (to instanceof Element && to.closest('.video-frame'))
            return;
        clearTimeout(timer);
        timer = null;
        shell()?.classList.remove('is-idle');
    }, true);
})();
