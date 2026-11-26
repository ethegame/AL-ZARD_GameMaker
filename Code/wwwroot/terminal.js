// 오른쪽 AI 터미널: xterm 1개 ↔ C# PTY (Views/MainWindow.Dev.cs, Services/ClaudeTerminal.cs).
// 엔진 고르기 [자동][CLAUDE][GPT][GEMINI] = 이 파일 (C# = Views/MainWindow.Ai.cs). 바꾸면 C# 이 터미널을 그 엔진으로 다시 띄움.
// 자동 = C# 이 새 세션마다 실제 엔진을 고름 → 탭에 [자동 · GPT] 처럼 고른 엔진이 보임
// 키 처리: Ctrl+C 복사(선택 있을 때) / Ctrl+V 붙여넣기 / Shift+Enter 줄바꿈.
// 맥: Cmd+C/V = 기본 복사·붙여넣기, Ctrl+C/V 는 그대로 AI 로 (Ctrl+V = 이미지 붙이기), Option = Meta
const $ = (id) => document.getElementById(id);
const isMac = 'mac' === Host.os;

function send(message) {
    Host.post(message);
}

const termOptions = {
    allowProposedApi: true, // unicode 애드온에 필요
    fontFamily: "'Cascadia Mono', 'D2Coding', Consolas, Menlo, 'SF Mono', 'Apple SD Gothic Neo', monospace",
    fontSize: 13,
    cursorBlink: true,
    scrollback: 10000,
    theme: { background: '#1e1e1e' },
};
if (isMac) {
    termOptions.macOptionIsMeta = true;   // Option+Enter 등 (Claude 줄바꿈)
} else {
    termOptions.windowsPty = { backend: 'conpty' };
}
const term = new Terminal(termOptions);
const fit = new FitAddon.FitAddon();
term.loadAddon(fit);
term.loadAddon(new Unicode11Addon.Unicode11Addon());
term.unicode.activeVersion = '11'; // 한글/CJK 2칸 폭

let running = false;

// 지금 실제 엔진(ai)과 고른 값(choice — 'auto' 포함). C# 이 켤 때·바꿀 때 'ai-engine' 으로 알려 줌
const AI_NAMES = { claude: 'Claude', gpt: 'GPT', gemini: 'GEMINI' };
const CHOICES = ['auto', 'claude', 'gpt', 'gemini'];
let ai = 'claude';
let choice = 'auto';

function aiName() {
    return AI_NAMES[ai] || 'Claude';
}

function applyEngine(engine, picked) {
    ai = AI_NAMES[engine] ? engine : 'claude';
    if (CHOICES.includes(picked)) {
        choice = picked;
    }
    $('ai-title').textContent = aiName();
    for (const tab of document.querySelectorAll('.ai-tab')) {
        tab.classList.toggle('on', tab.dataset.ai === choice);
        if ('auto' === tab.dataset.ai) {
            tab.textContent = 'auto' === choice ? `자동 · ${aiName().toUpperCase()}` : '자동';
        }
    }
}

// Claude 첫 줄 태그(🏷️ [GameMaker - Planner + Builder]) → teams.js 가 지금 일하는 팀 표시 (claude_context.md 가 붙이라고 함).
// PTY 출력은 조각나고 ANSI 색·커서 코드가 섞이므로 코드를 지운 꼬리 버퍼에서 찾는다
const TAG_PATTERN = /\[(?:AL-ZARD_)?GameMaker(?:\s*-\s*([A-Za-z +]+))?\]/g;
const TAG_TAIL = 400;
let tagTail = '';

function scanTeamTag(data) {
    // eslint-disable-next-line no-control-regex
    tagTail = (tagTail + data.replace(/\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07|\x1b[()][A-Z0-9]/g, '')).slice(-TAG_TAIL);
    let last = null;
    for (const match of tagTail.matchAll(TAG_PATTERN)) {
        last = match;
    }
    if (last) {
        tagTail = tagTail.slice(last.index + last[0].length);
        const tags = (last[1] || '').split('+').map((tag) => tag.trim()).filter(Boolean);
        window.dispatchEvent(new CustomEvent('gm-team-tag', { detail: tags }));
    }
    window.dispatchEvent(new Event('gm-term-output'));
}

function safeFit() {
    try {
        fit.fit();
    } catch {
        // 숨김 상태 등 크기 0 일 때 무시
    }
}

function setState(text, off) {
    $('term-state').textContent = text;
    $('term-state').classList.toggle('off', !!off);
}

function start(continueLast) {
    term.reset();
    setState('시작 중…');
    send({ t: 'term-start', continue: continueLast, cols: term.cols, rows: term.rows });
}

term.attachCustomKeyEventHandler((e) => {
    if ('keydown' !== e.type) {
        return true;
    }
    const ctrl = e.ctrlKey && !e.altKey && !e.metaKey && !isMac;
    const key = (e.key || '').toLowerCase();
    // Ctrl+C: 선택 영역이 있으면 복사, 없으면 그대로 ^C
    if (ctrl && 'c' === key && term.hasSelection()) {
        send({ t: 'term-copy', d: term.getSelection() });
        term.clearSelection();
        e.preventDefault();
        return false;
    }
    // Ctrl+V: C# 이 클립보드 텍스트를 돌려주면 term.paste (이미지만 있으면 C# 이 Alt+V 를 보냄)
    if (ctrl && 'v' === key) {
        send({ t: 'term-paste' });
        e.preventDefault();
        return false;
    }
    // Shift+Enter: LF → Claude Code 입력창 줄바꿈 (기본 \r 은 제출). IME 조합 중은 그대로
    if (e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey && 'Enter' === e.key && !e.isComposing) {
        send({ t: 'term-in', d: '\n' });
        e.preventDefault();
        return false;
    }
    return true;
});

term.onData((d) => {
    if (running) {
        send({ t: 'term-in', d });
    } else if ('\r' === d) {
        start(true); // 종료 후 Enter = 이어 하기
    }
});
term.onBinary((d) => send({ t: 'term-bin', d }));
term.onResize((size) => send({ t: 'term-resize', cols: size.cols, rows: size.rows }));

term.open($('term'));
new ResizeObserver(safeFit).observe($('term'));
safeFit();

for (const tab of document.querySelectorAll('.ai-tab')) {
    tab.onclick = () => {
        const next = tab.dataset.ai;
        if (next === choice) {
            return;
        }
        // 진행 중인 대화가 끊기므로 한 번 확인 (엔진마다 지난 대화가 따로라 돌아오면 [이어 하기] 로 이어짐).
        // 자동은 남은 사용량이 넉넉한 AI 를 고르는데, 그게 지금 AI 와 같으면 대화를 끊지 않음 (C# 이 판단)
        const target = 'auto' === next ? '자동(남은 사용량이 넉넉한 AI)' : AI_NAMES[next];
        if (running && next !== ai && !window.confirm(`지금 ${aiName()} 대화를 닫고 ${target} 로 바꿀까요?\n\n· 진행 중이던 작업은 멈춰요 (자동이 ${aiName()} 를 고르면 그대로 이어져요)\n· ${aiName()} 대화는 나중에 ${aiName()} 로 돌아와 [이어 하기] 로 이을 수 있어요\n· AI 들이 같은 팀 문서를 쓰니, 정한 것은 문서에 남아 있어요`)) {
            return;
        }
        send({ t: 'ai-engine-set', engine: next });
    };
}

$('btn-term-new').onclick = () => {
    if (running && !window.confirm(`지금 ${aiName()} 세션을 끝내고 새 대화로 시작할까요? (지금 대화는 [이어 하기] 로 돌아올 수 있습니다)`)) {
        return;
    }
    start(false);
    term.focus();
};
$('btn-term-continue').onclick = () => {
    start(true);
    term.focus();
};
Host.on((message) => {
    switch (message.t) {
        case 'term-out': {
            term.write(message.d);
            scanTeamTag(message.d);
            break;
        }
        case 'term-paste': {
            if (message.d) {
                term.paste(message.d);
            }
            break;
        }
        case 'ai-engine':
        case 'usage': {
            // 'usage' 도 고른 엔진을 같이 실어 와서, 'ai-engine' 알림을 놓쳐도 탭이 맞춰짐
            if (message.engine) {
                applyEngine(message.engine, message.choice);
            }
            break;
        }
        case 'term-reset': {
            // 프로젝트·엔진을 바꿈: 옛 화면 지우고 새 context 의 터미널이 곧 뜬다
            term.reset();
            tagTail = '';
            break;
        }
        case 'term-started': {
            running = true;
            if (message.engine) {
                applyEngine(message.engine);
            }
            $('btn-term-continue').hidden = true;
            setState(message.continueLast ? '지난 대화 이어 하기' : '새 대화');
            break;
        }
        case 'term-exit': {
            running = false;
            if (message.engine) {
                applyEngine(message.engine);
            }
            $('btn-term-continue').hidden = false;
            if (message.missing && 'gemini' === ai) {
                // Antigravity(agy) 가 없음 — 설치 안내 (각자 자기 구글 계정으로 로그인). Node.js 필요 없음
                setState('Antigravity 없음', true);
                const shell = isMac ? '터미널 앱' : '시작 메뉴에서 PowerShell';
                const installLine = isMac ? 'curl -fsSL https://antigravity.google/cli/install.sh | bash' : 'irm https://antigravity.google/cli/install.ps1 | iex';
                term.write('\r\n\x1b[33mGEMINI(Antigravity)를 찾지 못했어요.\x1b[0m\r\n'
                    + `1) ${shell}을 열고 아래 한 줄로 설치하세요:\r\n`
                    + `   ${installLine}\r\n`
                    + '2) 새 창에서 agy 를 한 번 실행해 내 구글 계정으로 로그인하세요.\r\n'
                    + '3) 여기서 Enter 를 누르면 다시 찾아서 시작해요. 그동안은 [자동] 이나 [CLAUDE]·[GPT] 를 써도 돼요.\r\n');
                break;
            }
            if (message.missing && 'gpt' === ai) {
                // Codex 가 없음 — 설치 안내 (각자 자기 계정으로 로그인). 깔고 Enter 를 누르면 앱을 다시 켜지 않아도 다시 찾음
                setState('Codex 없음', true);
                const shell = isMac ? '터미널 앱' : '시작 메뉴에서 PowerShell';
                const nodeLine = isMac ? 'brew install node' : 'winget install OpenJS.NodeJS.LTS';
                term.write('\r\n\x1b[33mCodex(GPT)를 찾지 못했어요.\x1b[0m\r\n'
                    + `1) Node.js(LTS)가 필요해요. 없으면 ${shell}을 열고 아래 한 줄로 설치한 뒤 창을 새로 열어요:\r\n`
                    + `   ${nodeLine}\r\n`
                    + `2) ${shell}에서 아래 한 줄로 Codex 를 설치하세요:\r\n`
                    + '   npm install -g @openai/codex@latest\r\n'
                    + '3) 새 창에서 codex 를 한 번 실행해 내 ChatGPT 계정으로 로그인하세요 (유료 플랜이나 API 키가 필요할 수 있어요).\r\n'
                    + '4) 여기서 Enter 를 누르면 다시 찾아서 시작해요.\r\n'
                    + '   (자세한 안내: https://developers.openai.com/codex/cli)\r\n');
                break;
            }
            if (message.missing) {
                // Claude Code 가 없음 — 설치 안내 (각자 자기 계정으로 로그인)
                setState('Claude Code 없음', true);
                term.write('\r\n\x1b[33mClaude Code 를 찾지 못했어요.\x1b[0m\r\n'
                    + '1) 시작 메뉴에서 PowerShell 을 열고 아래 한 줄을 붙여 넣어 설치하세요:\r\n'
                    + '   irm https://claude.ai/install.ps1 | iex\r\n'
                    + '2) 설치가 끝나면 새 PowerShell 창에서 claude 를 한 번 실행해 내 계정으로 로그인하세요 (Claude Pro 이상 유료 플랜 필요).\r\n'
                    + '3) AL-ZARD_GameMaker 를 닫았다 다시 켜면 여기서 Claude 가 시작됩니다.\r\n'
                    + '   (선택) Git for Windows(https://git-scm.com/downloads/win)를 깔아 두면 Claude 가 명령을 더 잘 다뤄요. 없어도 됩니다.\r\n'
                    + '   (자세한 안내: https://code.claude.com/docs/en/setup)\r\n');
                break;
            }
            setState(`종료됨 (exit ${message.code}) — Enter 또는 [이어 하기]`, true);
            term.write(`\r\n\x1b[33m[${aiName()} 종료됨 — Enter = 이어 하기, [새 세션] = 새 대화]\x1b[0m\r\n`);
            break;
        }
    }
});

// C# 은 처음 받은 term-created 로 PTY 를 띄운다 (지난 대화가 있으면 --continue)
send({ t: 'term-created', cols: term.cols, rows: term.rows });
term.focus();
