// Attaches dash.js to the player element for YouTube sources.
//
// YouTube publishes nothing above 360p as a single file, so HD arrives as separate video and
// audio tracks. The server describes them as a DASH manifest; dash.js feeds both into the one
// <video> element through Media Source Extensions. That keeps the element - and therefore the
// existing sync, the native controls and the ambient backlight - exactly as it was: seeking
// is still video.currentTime, and the media still comes from this origin, so drawing a frame
// into the ambient canvas does not taint it.
//
// The element is looked up on each call rather than cached, for the same reason ambient.js
// polls: Blazor re-renders whenever the circuit pushes state, and a held reference can go
// stale without warning.
(() => {
    'use strict';

    let player = null;

    function videoElement() {
        return document.querySelector('.video-frame video');
    }

    // iPadOS ships full MSE. iPhone Safari had none until 17.1 added ManagedMediaSource, and
    // dash.js v5 can drive that one too. Anything older gets the muxed fallback instead, so
    // the caller needs the answer before it commits to a source.
    function canPlayDash() {
        return typeof window.MediaSource !== 'undefined' ||
               typeof window.ManagedMediaSource !== 'undefined';
    }

    function teardown() {
        if (!player) return;
        try {
            player.destroy();
        } catch (e) {
            // A failed teardown must not stop the next source from loading.
            console.warn('dash.js teardown failed:', e);
        }
        player = null;
    }

    window.videoHome = window.videoHome || {};
    Object.assign(window.videoHome, {
        canPlayDash,

        // Returns false if dash.js cannot take over, which is the signal to fall back to the
        // muxed stream rather than leaving a dead element.
        attachDash(url) {
            const video = videoElement();
            if (!video || !canPlayDash() || typeof dashjs === 'undefined')
                return false;

            teardown();

            try {
                player = dashjs.MediaPlayer().create();

                player.updateSettings({
                    streaming: {
                        buffer: {
                            // Re-fetch already-buffered media at a higher quality once
                            // bandwidth allows, instead of playing out a stale low rung.
                            fastSwitchEnabled: true,
                        },
                        abr: {
                            // Start conservatively and climb. Opening at 1080p on a link that
                            // cannot hold it means stalling on the first few seconds, which
                            // reads as a broken video rather than a slow one.
                            initialBitrate: { video: 1200 },
                        },
                    },
                });

                // autoplay false: what plays and when is the sync layer's call, not the
                // player's - it pauses and seeks the element straight after this.
                player.initialize(video, url, false);

                player.on(dashjs.MediaPlayer.events.ERROR, e => console.error('dash.js error:', e));

                return true;
            } catch (e) {
                console.error('dash.js could not start:', e);
                teardown();
                return false;
            }
        },

        // Called before handing the element a plain file, so the MediaSource is released
        // rather than left holding its buffers.
        detachDash() {
            teardown();
        },
    });
})();
