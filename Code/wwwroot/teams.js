// 사장님 화면 4팀 현황판 (기획·프로그래밍 / 리소스·QA). C# = Views/MainWindow.Teams.cs
// - 각 칸 = 그 팀 현황.md (Claude 가 씀) 렌더. 머리줄 문구 = "## 지금" 의 첫 줄, 일하는 중이면 "작업 중"
// - 일하는 팀 = Claude 첫 줄 태그(terminal.js 'gm-team-tag') + ComfyUI 실행 중(리소스)
const $ = (id) => document.getElementById(id);
const ACTIVE_MS = 4000; // 터미널 출력이 이만큼 멎으면 "작업 중" 을 끈다
const TAG_TO_TEAM = { Planner: 'plan', Builder: 'prog', Developer: 'prog', Resource: 'res', Reviewer: 'qa' };

const state = {
    teams: [],
    none: false, // 프로젝트가 없음 (C# teams 메시지의 none)
    tagTeams: [],
    outputAt: 0,
    comfyRunning: false,
};

function send(message) {
    Host.post(message);
}

function quad(key) {
    return document.querySelector(`.quad[data-team="${key}"]`);
}

// "## 지금" 아래 첫 내용 줄 (목록 기호 제거)
function nowLine(markdown) {
    const lines = markdown.split(/\r?\n/);
    const start = lines.findIndex((line) => /^##\s*지금/.test(line));
    if (start < 0) {
        return '';
    }
    for (const line of lines.slice(start + 1)) {
        if (/^#/.test(line)) {
            break;
        }
        const text = line.replace(/^\s*([-*]|\d+\.)\s*/, '').trim();
        if (text) {
            return text;
        }
    }
    return '';
}

function renderTeams() {
    for (const team of state.teams) {
        const box = quad(team.key);
        if (!box) {
            continue;
        }
        box.dataset.now = nowLine(team.status);
        box.querySelector('.status').innerHTML = state.none
            ? '<p class="hint">프로젝트 없음 — 맨 위 [새 프로젝트] 를 눌러 주세요.</p>'
            : team.status
            ? window.DOMPurify.sanitize(window.marked.parse(team.status))
            : '<p class="hint">아직 현황이 없습니다. 오른쪽 AI 에게 일을 시키면 여기에 진행 상황이 올라옵니다.</p>';
    }
    renderWorking();
}

function renderWorking() {
    const active = Date.now() - state.outputAt < ACTIVE_MS;
    const working = new Set(active ? state.tagTeams : []);
    if (state.comfyRunning) {
        working.add('res');
    }
    for (const box of document.querySelectorAll('.quad')) {
        const on = working.has(box.dataset.team);
        box.classList.toggle('working', on);
        box.querySelector('.now').textContent = on ? `작업 중${box.dataset.now ? ` · ${box.dataset.now}` : ''}` : (box.dataset.now || '');
    }
    const names = [...new Set(state.tagTeams)]
        .map((key) => state.teams.find((team) => team.key === key)?.name)
        .filter(Boolean);
    $('team-now').textContent = active && names.length ? `● ${names.join(' + ')}` : '';
}

window.addEventListener('gm-team-tag', (event) => {
    state.tagTeams = event.detail.map((tag) => TAG_TO_TEAM[tag]).filter(Boolean);
    renderWorking();
});
// 상시 1초 폴링 대신 출력 이벤트 구동: 멎어 있다 출력이 오면 즉시 켜고, 마지막 출력 ACTIVE_MS 뒤 한 번만 다시 그려 끈다
let quietTimer = 0;
window.addEventListener('gm-term-output', () => {
    const wasActive = Date.now() - state.outputAt < ACTIVE_MS;
    state.outputAt = Date.now();
    if (!wasActive) {
        renderWorking();
    }
    clearTimeout(quietTimer);
    quietTimer = setTimeout(renderWorking, ACTIVE_MS + 50);
});

for (const box of document.querySelectorAll('.quad')) {
    box.querySelector('.btn-docs').onclick = () => send({ t: 'team-docs', team: box.dataset.team });
    box.querySelector('.btn-max').onclick = () => {
        const maxed = !box.classList.contains('max');
        for (const other of document.querySelectorAll('.quad')) {
            other.classList.toggle('max', maxed && other === box);
        }
        document.body.classList.toggle('maxed', maxed);
    };
}
$('btn-common').onclick = () => send({ t: 'team-docs' });

Host.on((message) => {
    switch (message.t) {
        case 'teams': {
            state.teams = message.teams;
            state.none = !!message.none;
            renderTeams();
            break;
        }
        case 'run-start': {
            state.comfyRunning = true;
            renderWorking();
            break;
        }
        case 'run-done':
        case 'run-fail': {
            state.comfyRunning = false;
            renderWorking();
            break;
        }
    }
});
