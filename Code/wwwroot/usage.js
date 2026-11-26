// 사용량 미터 한 줄 — 오른쪽 AI 칸 엔진 고르기 [자동][CLAUDE][GPT][GEMINI] 바로 아래. 지금 실제로 도는 엔진의 5 Hours · Weekly 막대를 보여 준다
// (자동이면 자동이 고른 엔진 것). 값 = C# 이 'usage' 로 세 엔진 것을 같이 보냄 (Views/UsageMonitor.cs), 도는 엔진 = 'ai-engine' (terminal.js 와 같은 메시지).
// 마우스를 올리면 다시 차는 시각·받은 시각. 다시 차는 시각이 지난 값은 "–" 로 (1분마다 다시 그림)
const $ = (id) => document.getElementById(id);

const WARN_PERCENT = 80;
const HOT_PERCENT = 95;
const WEEKDAYS = ['일', '월', '화', '수', '목', '금', '토'];
const NAMES = { claude: 'Claude', gpt: 'GPT', gemini: 'GEMINI' };

let engine = 'claude';
let data = { claude: null, gpt: null, gemini: null };

function pad(n) {
    return String(n).padStart(2, '0');
}

function clock(ms) {
    const d = new Date(ms);
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function dateClock(ms) {
    const d = new Date(ms);
    return `${pad(d.getMonth() + 1)}/${pad(d.getDate())} (${WEEKDAYS[d.getDay()]}) ${clock(ms)}`;
}

function level(percent) {
    if (percent >= HOT_PERCENT) {
        return 'hot';
    }
    return percent >= WARN_PERCENT ? 'warn' : '';
}

function isLive(limit) {
    return null === limit.resetsAt || limit.resetsAt > Date.now();
}

// 짧은 창(하루 이하) = "21% · 14:00"(사용 · 다시 차는 시각), 긴 창 = "남음 97%"
function valueText(limit) {
    const percent = Math.min(100, Math.max(0, limit.percent));
    const short = null !== limit.minutes && limit.minutes <= 24 * 60;
    if (!short) {
        return `남음 ${Math.round((100 - percent) * 10) / 10}%`;
    }
    const used = `${Math.round(percent * 10) / 10}%`;
    return null !== limit.resetsAt ? `${used} · ${clock(limit.resetsAt)}` : used;
}

function item(limit) {
    const percent = Math.min(100, Math.max(0, limit.percent));
    const box = document.createElement('div');
    box.className = 'um-item';
    const label = document.createElement('span');
    label.className = 'um-label';
    label.textContent = limit.label;
    const bar = document.createElement('span');
    bar.className = 'um-bar';
    const fill = document.createElement('span');
    fill.className = `um-fill ${level(percent)}`.trim();
    fill.style.width = `${percent}%`;
    bar.appendChild(fill);
    const value = document.createElement('span');
    value.className = `um-value ${level(percent)}`.trim();
    value.textContent = valueText(limit);
    box.append(label, bar, value);
    return box;
}

function tooltip(name, body, windows) {
    const lines = [`${name} 사용량 (계정 단위 — 이 앱 밖에서 쓴 것도 포함)`];
    for (const limit of windows) {
        const used = Math.round(limit.percent * 10) / 10;
        const resets = null !== limit.resetsAt ? `다시 차는 시각 ${dateClock(limit.resetsAt)}` : '다시 차는 시각 모름';
        lines.push(`${limit.label}: ${used}% 사용 · ${resets}`);
    }
    for (const detail of (body && body.details) || []) {
        lines.push(detail);
    }
    if (body && body.received) {
        lines.push(`받은 시각 ${dateClock(body.received)}`);
    }
    if (0 === windows.length && body && body.hint) {
        lines.push(body.hint);
    }
    return lines.join('\n');
}

function render() {
    const meter = $('usage-meter');
    const body = data[engine];
    const windows = ((body && body.windows) || []).filter(isLive);
    meter.replaceChildren();
    if (0 === windows.length) {
        const empty = document.createElement('div');
        empty.className = 'um-item';
        empty.textContent = '사용량 –';
        meter.appendChild(empty);
    } else {
        for (const limit of windows) {
            meter.appendChild(item(limit));
        }
    }
    meter.title = tooltip(NAMES[engine] || 'Claude', body, windows);
}

Host.on((message) => {
    if ('usage' === message.t) {
        data = { claude: message.claude, gpt: message.gpt, gemini: message.gemini };
        if (NAMES[message.engine]) {
            engine = message.engine;
        }
        render();
    } else if ('ai-engine' === message.t) {
        engine = NAMES[message.engine] ? message.engine : 'claude';
        render();
    }
});

render();
setInterval(render, 60 * 1000);
