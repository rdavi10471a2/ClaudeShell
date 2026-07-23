// ---------------------------------------------------------------------------------------------
// Column splitters
//
// ONE rule, and every past bug here came from breaking it: SIZE THE LEADING PANE ONLY.
//
// The trailing pane must stay `flex: 1 1 auto; min-width: 0` so it consumes exactly what is left.
// The moment you also pin the trailing pane to a pixel width, the two panes stop being related to
// the container: drag the divider right, or shrink the window, and leading + trailing exceed the
// layout width. The overflow goes off the right edge â€” and because the layout is `overflow: hidden`,
// it is silently clipped rather than scrolled. That took the transcript's vertical scrollbar
// off-screen with it, which is why "the chat lost its scrollbar" and "the right side runs off the
// screen" were one bug, not two.
//
// So the leading width is clamped against what the container can actually give:
//     max leading = layout width - splitter width - minTrailing
// which means the divider stops before it can push the trailing pane out, instead of being free to
// run past the edge.
//
// The same clamp is re-applied when the layout resizes (ResizeObserver, not a window listener â€”
// it is owned by the element, so it dies with the element instead of accumulating one dead global
// handler per tab switch). Shrinking the window therefore pulls the divider back in rather than
// shoving the trailing pane off-screen.
function attachColumnSplitter(layout, leading, trailing, splitter, minLeading, minTrailing) {
    if (!layout || !leading || !trailing || !splitter) {
        return;
    }

    // Keyed on the element, not on a C# bool in the caller: the element is what actually carries
    // the listener, so a re-created layout gets a fresh splitter and re-attaches correctly, while
    // a re-render of the same element does not stack a second pointerdown handler.
    if (splitter.dataset.resizeAttached === "true") {
        return;
    }

    splitter.dataset.resizeAttached = "true";

    let startX = 0;
    let startLeadingWidth = 0;

    function clamp(width) {
        const splitterWidth = splitter.getBoundingClientRect().width || 12;
        const available = layout.clientWidth - splitterWidth;

        // A container too small to honour both minimums cannot be satisfied; keep the leading pane
        // at its minimum and let the trailing pane take the remainder. Panes declare their own
        // internal overflow, so the content scrolls instead of the layout overflowing.
        const maxLeading = Math.max(minLeading, available - minTrailing);
        return Math.min(maxLeading, Math.max(minLeading, width));
    }

    function apply(width) {
        const next = clamp(width);
        leading.style.flex = `0 0 ${next}px`;
        leading.style.width = `${next}px`;

        // Explicitly (re)assert the trailing contract. Cheap, and it repairs any pinned width left
        // behind by an older build or a previously shared splitter implementation.
        trailing.style.flex = "1 1 auto";
        trailing.style.width = "";
        trailing.style.minWidth = "0";
    }

    function onPointerMove(event) {
        apply(startLeadingWidth + (event.clientX - startX));
    }

    function onPointerUp() {
        splitter.classList.remove("dragging");
        document.body.style.cursor = "";
        document.body.style.userSelect = "";
        window.removeEventListener("pointermove", onPointerMove);
        window.removeEventListener("pointerup", onPointerUp);
    }

    splitter.addEventListener("pointerdown", event => {
        event.preventDefault();
        startX = event.clientX;
        startLeadingWidth = leading.getBoundingClientRect().width;
        splitter.classList.add("dragging");
        document.body.style.cursor = "col-resize";
        document.body.style.userSelect = "none";
        window.addEventListener("pointermove", onPointerMove);
        window.addEventListener("pointerup", onPointerUp);
    });

    if (typeof ResizeObserver === "function") {
        const observer = new ResizeObserver(() => {
            const current = leading.getBoundingClientRect().width;
            if (current > 0) {
                apply(current);
            }
        });

        observer.observe(layout);
        splitter.__columnSplitterObserver = observer;
    }
}

// Assistant tab: composer on the left, chat history on the right.
export function attachAssistantSplitter(layout, composer, transcript, splitter) {
    attachColumnSplitter(layout, composer, transcript, splitter, 320, 360);
}

export function attachComposerAutoScroll(textarea) {
    if (!textarea || textarea.dataset.autoScrollAttached === "true") {
        return;
    }

    textarea.dataset.autoScrollAttached = "true";
    textarea.addEventListener("input", () => {
        textarea.scrollTop = textarea.scrollHeight;
    });
}


export function scrollElementToBottom(element) {
    if (!element) {
        return;
    }

    const scroll = (attempt = 0) => {
        window.requestAnimationFrame(() => {
            element.scrollTop = element.scrollHeight;
            if (attempt < 4) {
                window.setTimeout(() => scroll(attempt + 1), 40);
            }
        });
    };

    scroll();
}

export async function copyTextToClipboard(text) {
    if (!text) {
        return;
    }

    if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return;
    }

    const textarea = document.createElement("textarea");
    textarea.value = text;
    textarea.setAttribute("readonly", "");
    textarea.style.position = "fixed";
    textarea.style.left = "-9999px";
    textarea.style.top = "0";
    document.body.appendChild(textarea);
    textarea.select();
    document.execCommand("copy");
    document.body.removeChild(textarea);
}


export function openHtmlDocument(html, title) {
    const popup = window.open("", "_blank");
    if (!popup) {
        return;
    }

    popup.opener = null;
    popup.document.open();
    popup.document.write(html || "");
    popup.document.title = title || popup.document.title;
    popup.document.close();
}


export function setBeforeUnloadGuard(enabled, message) {
    if (enabled) {
        window.__codingServicesBeforeUnloadMessage = message || "Refreshing will reset the current Coding Services session.";
        if (!window.__codingServicesBeforeUnloadHandler) {
            window.__codingServicesBeforeUnloadHandler = event => {
                event.preventDefault();
                event.returnValue = window.__codingServicesBeforeUnloadMessage;
                return window.__codingServicesBeforeUnloadMessage;
            };
            window.addEventListener("beforeunload", window.__codingServicesBeforeUnloadHandler);
        }

        return;
    }

    if (window.__codingServicesBeforeUnloadHandler) {
        window.removeEventListener("beforeunload", window.__codingServicesBeforeUnloadHandler);
        window.__codingServicesBeforeUnloadHandler = null;
    }

    window.__codingServicesBeforeUnloadMessage = "";
}


// --- Lightweight code-block syntax highlighting -----------------------------
// A tiny in-house tokenizer (no CDN, no Prism/hljs): keyword/string/comment/number
// coloring for a handful of common languages. Runs over the transcript's <pre><code>
// blocks after each render; a ```mermaid fence is skipped (renderMermaidBlocks owns it).
const HL_KEYWORDS = {
    csharp: "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach get goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield nameof when where",
    sql: "select from where join inner left right full outer cross apply on group by having order asc desc insert into values update set delete create table view index alter drop add column primary key foreign references not null default distinct union all as and or in exists between like is case when then else end with cast convert count sum avg min max top limit offset over partition begin commit rollback declare",
    javascript: "await async function return if else for while do break continue const let var new class extends super this typeof instanceof in of null undefined true false void delete try catch finally throw switch case default yield import export from get set static",
    json: "true false null",
    bash: "if then else elif fi for while do done case esac function return in export local set echo cd",
    python: "def class return if elif else for while break continue import from as pass raise try except finally with lambda yield global nonlocal in is not and or None True False del assert async await",
};

function hlKeywordSet(lang) {
    const alias = { cs: "csharp", ts: "javascript", typescript: "javascript", js: "javascript",
        sh: "bash", powershell: "bash", ps1: "bash", jsonc: "json", py: "python" };
    const words = HL_KEYWORDS[alias[lang] || lang];
    return words ? new Set(words.split(" ")) : null;
}
function hlEscape(s) {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}
function hlLineComment(lang) {
    if (lang === "sql") return "--";
    if (["bash", "sh", "powershell", "ps1", "python", "py", "yaml", "yml", "ini", "toml"].includes(lang)) return "#";
    return "//";
}

function hlTokenize(code, lang) {
    const kw = hlKeywordSet(lang);
    const lc = hlLineComment(lang).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    // Priority: line comment, block comment, string, number, identifier, single char.
    const re = new RegExp(
        "(" + lc + "[^\\n]*)" +
        "|(/\\*[\\s\\S]*?\\*/)" +
        "|(\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|`(?:\\\\.|[^`\\\\])*`)" +
        "|(\\b\\d[\\d_.]*\\b)" +
        "|([A-Za-z_$][\\w$]*)" +
        "|([\\s\\S])", "g");
    let out = "";
    let m;
    while ((m = re.exec(code)) !== null) {
        if (m[1] || m[2]) {
            out += '<span class="hl-com">' + hlEscape(m[0]) + "</span>";
        } else if (m[3]) {
            out += '<span class="hl-str">' + hlEscape(m[0]) + "</span>";
        } else if (m[4]) {
            out += '<span class="hl-num">' + hlEscape(m[0]) + "</span>";
        } else if (m[5]) {
            const token = m[0];
            const isKeyword = kw && kw.has(lang === "sql" ? token.toLowerCase() : token);
            out += isKeyword ? '<span class="hl-kw">' + hlEscape(token) + "</span>" : hlEscape(token);
        } else {
            out += hlEscape(m[0]);
        }
    }
    return out;
}

export function highlightCodeBlocks(container) {
    if (!container) {
        return;
    }

    container.querySelectorAll("pre code").forEach(block => {
        try {
            if (block.dataset.hl === "1") {
                return;
            }
            // A ```mermaid fence is a diagram, not code — renderMermaidBlocks owns it.
            if (/\blanguage-mermaid\b/i.test(block.className || "")) {
                return;
            }
            block.dataset.hl = "1";
            const match = (block.className || "").match(/language-([a-z0-9#+]+)/i);
            let lang = match ? match[1].toLowerCase() : "";
            if (lang === "c#") {
                lang = "csharp";
            }
            block.innerHTML = hlTokenize(block.textContent || "", lang);
        } catch (e) {
            // Leave the block as plain text on any tokenizer error.
        }
    });
}


// --- Mermaid diagrams -------------------------------------------------------
// A ```mermaid fence becomes <pre class="mermaid">SOURCE</pre> (Markdig's advanced
// diagram extension). We render it to inline SVG with the locally-vendored mermaid
// bundle (a classic <script> that sets window.mermaid). securityLevel:'strict' matters:
// the diagram source is UNTRUSTED model output, so labels are sanitized and click/script
// directives are inert. On any parse error we leave the raw text, never a blank.
let mermaidReady = false;
let mermaidSeq = 0;

function ensureMermaid() {
    if (!window.mermaid) {
        return false;
    }
    if (!mermaidReady) {
        window.mermaid.initialize({
            startOnLoad: false,
            securityLevel: "strict",
            theme: "default",
            fontFamily: "inherit",
        });
        mermaidReady = true;
    }
    return true;
}

// The vendored bundle is ~3.5MB, so the first render pass can fire before window.mermaid
// finishes parsing. Poll briefly rather than give up (and no-op forever) on that race.
async function waitForMermaid(tries) {
    for (let i = 0; i < tries; i++) {
        if (window.mermaid) {
            return true;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    return !!window.mermaid;
}

export async function renderMermaidBlocks(container) {
    if (!container) {
        return;
    }

    const blocks = container.querySelectorAll("pre.mermaid");
    if (blocks.length === 0) {
        return;
    }

    const loaded = window.mermaid ? true : await waitForMermaid(20);
    for (const pre of blocks) {
        if (pre.dataset.mermaid === "1") {
            continue;
        }

        const source = pre.textContent || "";

        if (!loaded) {
            // Bundle never arrived (404 / global not set). Leave a visible note instead of
            // silently showing the raw fence, and allow a later pass to retry.
            const note = document.createElement("div");
            note.className = "mermaid-error";
            note.textContent = "⚠ Mermaid did not load (window.mermaid undefined) — diagram not rendered.";
            pre.replaceWith(note);
            continue;
        }

        pre.dataset.mermaid = "1";
        ensureMermaid();
        const host = document.createElement("div");
        host.className = "mermaid-rendered";

        try {
            mermaidSeq += 1;
            const { svg } = await window.mermaid.render("mmd-" + mermaidSeq, source);
            host.innerHTML = svg;
            pre.replaceWith(host);
        } catch (e) {
            // The diagram itself is malformed: surface the parse error and keep the source.
            const note = document.createElement("div");
            note.className = "mermaid-error";
            note.textContent = "⚠ Mermaid parse error: " + (e && e.message ? e.message : e);
            const raw = document.createElement("pre");
            raw.textContent = source;
            pre.replaceWith(note, raw);
        }
    }
}
