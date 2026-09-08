/* ==========================================================================
   SeasonEngine website - shared behaviour
   No dependencies. Safe to include on every page.

   Provides:
   1. theme toggle              (button.se-theme, remembered in localStorage)
   2. mobile navigation         (button.se-nav__toggle + .se-nav__links)
   3. code blocks              (copy button + lightweight C#/bash/xml colouring)
   4. table of contents        (aside[data-toc] filled from [data-toc-source])
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
        try { localStorage.setItem('se-theme', theme); } catch (e) { /* private mode */ }

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

    /* --- 3. code blocks -------------------------------------------------- */

    var KEYWORDS = {
        csharp: ['abstract', 'as', 'async', 'await', 'base', 'bool', 'break', 'byte', 'case', 'catch', 'class',
            'const', 'continue', 'default', 'do', 'double', 'else', 'enum', 'event', 'explicit', 'false', 'finally',
            'float', 'for', 'foreach', 'get', 'if', 'in', 'int', 'interface', 'internal', 'is', 'lock', 'long',
            'namespace', 'new', 'null', 'object', 'out', 'override', 'params', 'partial', 'private', 'protected',
            'public', 'readonly', 'record', 'ref', 'return', 'sealed', 'set', 'static', 'string', 'struct', 'switch',
            'this', 'throw', 'true', 'try', 'typeof', 'uint', 'ulong', 'using', 'var', 'virtual', 'void', 'while'],
        bash: ['cd', 'dotnet', 'git', 'npx', 'python', 'sudo', 'export', 'echo']
    };

    function escapeHtml(text) {
        return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function tokenPattern(lang) {
        var words = KEYWORDS[lang];
        var parts = [
            '(\\/\\/[^\\n]*|\\/\\*[\\s\\S]*?\\*\\/|(?:^|\\s)#[^\\n]*)',       // 1 comment
            '("(?:\\\\.|[^"\\\\\\n])*"|\'(?:\\\\.|[^\'\\\\\\n])*\')',          // 2 string
            '\\b(0x[0-9a-fA-F]+|\\d+(?:\\.\\d+)?f?)\\b'                        // 3 number
        ];
        parts.push(words ? '\\b(' + words.join('|') + ')\\b' : '(\\u0000)');   // 4 keyword
        parts.push('\\b([A-Z][A-Za-z0-9_]*)\\b');                              // 5 type-ish
        return new RegExp(parts.join('|'), 'g');
    }

    var CLASSES = ['tok-comment', 'tok-string', 'tok-number', 'tok-keyword', 'tok-type'];

    function highlight(code, lang) {
        if (lang === 'text' || lang === 'none') return escapeHtml(code);

        if (lang === 'xml') {
            return escapeHtml(code)
                .replace(/(&lt;\/?)([\w.:-]+)/g, '$1<span class="tok-keyword">$2</span>')
                .replace(/([\w.:-]+)=(&quot;|")((?:[^"&]|&(?!quot;))*)(&quot;|")/g,
                    '<span class="tok-type">$1</span>=<span class="tok-string">"$3"</span>');
        }

        var pattern = tokenPattern(lang === 'bash' ? 'bash' : 'csharp');
        var out = '';
        var last = 0;
        var match;

        while ((match = pattern.exec(code)) !== null) {
            out += escapeHtml(code.slice(last, match.index));

            for (var group = 1; group <= 5; group++) {
                if (match[group] === undefined) continue;

                var raw = match[group];
                var lead = '';
                if (group === 1) {
                    var offset = match[0].indexOf(raw);
                    lead = escapeHtml(match[0].slice(0, offset));
                }
                out += lead + '<span class="' + CLASSES[group - 1] + '">' + escapeHtml(raw) + '</span>';
                break;
            }

            last = match.index + match[0].length;
        }

        return out + escapeHtml(code.slice(last));
    }

    function initCode() {
        var blocks = document.querySelectorAll('pre.se-code > code');

        for (var i = 0; i < blocks.length; i++) {
            var code = blocks[i];
            var lang = 'csharp';
            var match = /language-([\w-]+)/.exec(code.className || '');
            if (match) lang = match[1];

            code.innerHTML = highlight(code.textContent, lang);

            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'se-code__copy';
            button.textContent = 'Copy';
            button.setAttribute('aria-label', 'Copy code to clipboard');
            bindCopy(button, code);
            code.parentNode.appendChild(button);
        }
    }

    function bindCopy(button, code) {
        button.addEventListener('click', function () {
            var text = code.textContent;

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

    function initToc() {
        var host = document.querySelector('[data-toc]');
        if (!host) return;

        var source = document.querySelector('[data-toc-source]');
        if (!source) return;

        var headings = source.querySelectorAll('h2, h3');
        if (headings.length < 2) { host.style.display = 'none'; return; }

        var list = document.createElement('ul');
        var links = [];

        for (var i = 0; i < headings.length; i++) {
            var heading = headings[i];
            if (!heading.id) heading.id = slug(heading.textContent) || 'section-' + i;

            var item = document.createElement('li');
            if (heading.tagName === 'H3') item.className = 'is-h3';

            var link = document.createElement('a');
            link.href = '#' + heading.id;
            link.textContent = heading.textContent;
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
        initCode();
        initToc();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
