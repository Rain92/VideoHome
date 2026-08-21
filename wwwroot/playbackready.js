// Tells the sync layer when this browser could actually start playing where the
// playhead now sits.
//
// A seek is not the end of the story: the browser - or dash.js, feeding the element
// through MSE - still has to fetch media at the new position before anything can play
// there. Whoever did the seeking usually has that data already, so it resumes at once
// while the other side is still fetching, and from then on the two run apart by however
// long that fetch took. Nobody resumes until every client has answered this.
//
// The element is looked up per call for the same reason ambient.js and dashplayer.js do
// it: Blazor re-renders whenever the circuit pushes state, and a held reference can go
// stale without warning.
(() => {
    'use strict';

    // How much media past the playhead counts as "will not stall the moment it starts".
    // Capped by what is actually left of the video, so a sync point in the final second
    // can still be satisfied.
    const AHEAD_SECONDS = 1.0;

    // HTMLMediaElement.readyState: there is data at the playhead *and* enough after it
    // to carry on. HAVE_CURRENT_DATA (2) is one frame and would stall immediately.
    const HAVE_FUTURE_DATA = 3;

    // Only the newest sync point matters; an earlier wait is abandoned when one arrives.
    let pendingWait = null;

    function videoElement() {
        return document.querySelector('.video-frame video');
    }

    // Seconds of continuous media buffered from `position` onwards, 0 if that spot is
    // not covered at all. The tolerance on the range start is because after a seek the
    // buffered range routinely begins a few milliseconds past what was asked for.
    function bufferedAhead(video, position) {
        const ranges = video.buffered;
        for (let i = 0; i < ranges.length; i++) {
            if (ranges.start(i) <= position + 0.25 && ranges.end(i) > position)
                return ranges.end(i) - position;
        }
        return 0;
    }

    function isReady(video) {
        // A seek still in flight means currentTime is already the target but the media
        // for it may not be there yet, and readyState still describes the old position.
        if (video.seeking)
            return false;

        const position = video.currentTime;
        const duration = Number.isFinite(video.duration) ? video.duration : Infinity;

        // Never ask for more than the video has left, or the end of a file could
        // never satisfy this and every client would sit out the full timeout.
        const needed = Math.max(0, Math.min(AHEAD_SECONDS, duration - position - 0.1));

        return video.readyState >= HAVE_FUTURE_DATA && bufferedAhead(video, position) >= needed;
    }

    window.videoHome = window.videoHome || {};
    Object.assign(window.videoHome, {
        // Resolves true once playback could start without stalling, false if that has
        // not happened within timeoutMs. False is not an error: the caller reports in
        // regardless, because one client that cannot buffer must not hold the rest
        // paused indefinitely.
        waitUntilReady(timeoutMs) {
            if (pendingWait)
                pendingWait();

            const video = videoElement();
            if (!video)
                return Promise.resolve(false);

            // A dead source will never buffer. Answering "ready" gets everyone else
            // moving instead of making them wait out the timeout for this client.
            if (video.error) {
                console.warn('videoHome: the player is in an error state; reporting ready anyway.');
                return Promise.resolve(true);
            }

            // The usual case once someone has watched a stretch already. Checking up
            // front matters: the events below have all fired by then and waiting for
            // another one would burn the whole timeout on an element that is ready.
            if (isReady(video))
                return Promise.resolve(true);

            return new Promise(resolve => {
                // progress covers plain buffering with no state change of its own;
                // seeked is what ends the video.seeking guard above.
                const events = ['progress', 'canplay', 'canplaythrough', 'loadeddata', 'seeked', 'playing'];
                let settled = false;
                let timer = 0;

                const finish = value => {
                    if (settled) return;
                    settled = true;
                    clearTimeout(timer);
                    events.forEach(name => video.removeEventListener(name, check));
                    if (pendingWait === abandon) pendingWait = null;
                    resolve(value);
                };

                const check = () => {
                    if (video.error || isReady(video))
                        finish(true);
                };

                // Superseded by a newer sync point: resolve false and stop listening.
                // The answer is discarded on the .NET side anyway - it no longer matches
                // the version being waited on - but the listeners have to come off.
                const abandon = () => finish(false);

                timer = setTimeout(() => finish(false), Math.max(250, timeoutMs | 0));
                pendingWait = abandon;
                events.forEach(name => video.addEventListener(name, check));

                // One more time now the listeners are attached, in case it became ready
                // in between - otherwise that transition is missed and this waits for an
                // event that has already been and gone.
                check();
            });
        },
    });
})();
