/* ==========================================================================
   SeasonStudio website - shared behaviour
   No dependencies. Safe to include on every page.

   Sibling of the SeasonEngine site's assets/js/site.js. Two deliberate
   differences: the theme is stored under its own key so moving between the two
   sites does not drag one site's theme onto the other, and copyable blocks
   include prompt cards as well as code blocks.

   Provides:
   1. theme toggle              (button.se-theme, remembered in localStorage)
   2. mobile navigation         (button.se-nav__toggle + .se-nav__links)
   3. copyable blocks           (pre.se-code and .st-prompt get a copy button)
   4. table of contents         (aside[data-toc] filled from [data-toc-source])
   ========================================================================== */

(function () {
    'use strict';

    /* --- 1. theme --------------------------------------------------------- */

    /* Dark is the site default. The fallback here only matters if the inline
       boot script in <head> failed to run, so it must agree with it. */
    function currentTheme() {
        return document.documentElement.getAttribute('data-theme') || 'dark';
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem('st-theme', theme); } catch (e) { /* private mode */ }

        var icon = theme === 'dark' ? 'Moon.png' : 'Sun.png';
        var imgs = document.querySelectorAll('.se-theme img');
        for (var i = 0; i < imgs.length; i++) {
            imgs[i].setAttribute('src', imgs[i].getAttribute('src').replace(/(Sun|Moon)\.png/, icon));
            imgs[i].setAttribute('alt', theme === 'dark' ? 'Dark theme' : 'Light theme');
        }
    }

    function initTheme() {
        applyTheme(currentTheme());

        var buttons = document.querySelectorAll('.se-theme');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].addEventListener('click', function () {
                applyTheme(currentTheme() === 'dark' ? 'light' : 'dark');
            });
        }
    }

    /* --- 2. mobile navigation -------------------------------------------- */

    function initNav() {
        var toggle = document.querySelector('.se-nav__toggle');
        var links = document.querySelector('.se-nav__links');
        if (!toggle || !links) return;

        toggle.addEventListener('click', function () {
            var open = links.classList.toggle('is-open');
            toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        });
    }

    /* --- 3. copyable blocks ---------------------------------------------- */

    /* Prompts are the one thing on this site a visitor actually wants to take
       away, so the copy button is attached to prompt cards as well. For a card
       the copied text is the prompt line only - not the labels around it. */
    function initCopy() {
        var blocks = document.querySelectorAll('pre.se-code');

        for (var i = 0; i < blocks.length; i++) {
            addCopyButton(blocks[i], blocks[i]);
        }

        var cards = document.querySelectorAll('.st-prompt');
        for (var j = 0; j < cards.length; j++) {
            var value = cards[j].querySelector('[data-copy]');
            if (value) addCopyButton(cards[j].querySelector('pre.se-code') || cards[j], value);
        }
    }

    function addCopyButton(host, source) {
        if (host.querySelector('.se-code__copy')) return;

        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'se-code__copy';
        button.textContent = 'Copy';
        button.setAttribute('aria-label', 'Copy to clipboard');
        bindCopy(button, source);

        if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
        host.appendChild(button);
    }

    function bindCopy(button, source) {
        button.addEventListener('click', function () {
            var text = (source.textContent || '').trim();

            var done = function () {
                button.textContent = 'Copied';
                setTimeout(function () { button.textContent = 'Copy'; }, 1600);
            };

            // The clipboard API rejects when the document does not have focus,
            // which happens often enough in embedded browsers. Fall through to
            // the textarea route instead of reporting failure.
            var legacy = function () {
                var area = document.createElement('textarea');
                area.value = text;
                area.setAttribute('readonly', '');
                area.style.position = 'fixed';
                area.style.opacity = '0';
                document.body.appendChild(area);
                area.select();
                try {
                    if (document.execCommand('copy')) done();
                    else button.textContent = 'Failed';
                } catch (e) {
                    button.textContent = 'Failed';
                }
                document.body.removeChild(area);
            };

            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(done, legacy);
                return;
            }

            legacy();
        });
    }

    /* --- 4. table of contents -------------------------------------------- */

    function slug(text) {
        return text.toLowerCase().replace(/[^a-z0-9\s-]/g, '').trim().replace(/\s+/g, '-').slice(0, 48);
    }

    /* Headings on this site often carry a maturity badge ("Video Experimental").
       The badge belongs in the heading but not in the contents list, so read the
       heading with its badges stripped out. */
    function headingLabel(heading) {
        var copy = heading.cloneNode(true);
        var badges = copy.querySelectorAll('.se-badge');
        for (var i = 0; i < badges.length; i++) {
            badges[i].parentNode.removeChild(badges[i]);
        }
        return (copy.textContent || '').replace(/\s+/g, ' ').trim();
    }

    function initToc() {
        var host = document.querySelector('[data-toc]');
        if (!host) return;

        var source = document.querySelector('[data-toc-source]');
        if (!source) return;

        var headings = source.querySelectorAll('h2');
        if (headings.length < 2) { host.style.display = 'none'; return; }

        var list = document.createElement('ul');
        var links = [];

        for (var i = 0; i < headings.length; i++) {
            var heading = headings[i];
            var label = headingLabel(heading);
            if (!heading.id) heading.id = slug(label) || 'section-' + i;

            var item = document.createElement('li');

            var link = document.createElement('a');
            link.href = '#' + heading.id;
            link.textContent = label;
            item.appendChild(link);
            list.appendChild(item);
            links.push({ link: link, heading: heading });
        }

        host.appendChild(list);

        // Highlight the section being read. An IntersectionObserver would only
        // fire while a heading crosses a band, leaving nothing marked once you
        // stop scrolling inside a long section, so track position directly:
        // the active heading is the last one that has passed the top of the
        // viewport.
        var pending = false;

        function markActive() {
            pending = false;

            var active = 0;
            for (var i = 0; i < links.length; i++) {
                if (links[i].heading.getBoundingClientRect().top <= 120) active = i;
            }

            // At the very bottom the last section may never reach the line.
            if (window.innerHeight + window.pageYOffset >= document.body.scrollHeight - 4) {
                active = links.length - 1;
            }

            for (var j = 0; j < links.length; j++) {
                links[j].link.classList.toggle('is-active', j === active);
            }
        }

        function schedule() {
            if (pending) return;
            pending = true;
            window.requestAnimationFrame(markActive);
        }

        markActive();
        window.addEventListener('scroll', schedule, { passive: true });
        window.addEventListener('resize', schedule);
    }

    /* --- boot ------------------------------------------------------------ */

    function boot() {
        initTheme();
        initNav();
        initCopy();
        initToc();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
