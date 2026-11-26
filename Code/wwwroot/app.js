// GameMaker 화면. C# (Views/MainWindow.cs) 과 메시지로만 대화한다 (통로 = bridge.js 의 Host).
import { Viewer, MODEL_EXTENSIONS } from './viewer.js';

const $ = (id) => document.getElementById(id);
const IMAGE_EXTENSIONS = ['.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp'];
const AUDIO_EXTENSIONS = ['.mp3', '.wav', '.ogg', '.flac', '.opus'];
const SEED_KEYS = ['seed', 'noise_seed'];
const MULTILINE_KEYS = ['text', 'prompt', 'positive', 'negative', 'string'];

const state = {
    workflow: null,      // { name, nodes:[{id, classType, title, fields:[{key, kind, value}]}] }
    values: {},          // { nodeId: { key: value } } — 기본값과 다른 것만
    randomSeed: {},      // { "nodeId.key": true }
    running: false,
    serverOn: false,
    runs: [],
    currentRun: null,
};

const viewer = new Viewer($('canvas'));

function send(message) {
    Host.post(message);
}

// ---------- 저장 (이 PC 의 이 화면에서만 쓰는 편의 — 없어도 동작) ----------
function storageGet(key, fallback) {
    try {
        const raw = localStorage.getItem(key);
        return null === raw ? fallback : JSON.parse(raw);
    } catch {
        return fallback;
    }
}

function storageSet(key, value) {
    try {
        localStorage.setItem(key, JSON.stringify(value));
    } catch {
        // 저장 불가 환경이면 무시
    }
}

// ---------- 알림 ----------
let toastTimer = 0;
function toast(text, info = false) {
    const box = $('toast');
    box.textContent = text;
    box.className = info ? 'info' : '';
    box.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { box.hidden = true; }, info ? 3000 : 12000);
}
$('toast').onclick = () => { $('toast').hidden = true; };

// ---------- 서버 ----------
function setServer(stateName, url, owned, installed) {
    state.serverOn = 'on' === stateName;
    // 꺼져 있고 설정된 ComfyUI 도 없음 = 그림 도구 없음 → 리소스 칸·작업대에 [설치 방법] (comfy.js)
    const missing = 'off' === stateName && false === installed;
    for (const box of document.querySelectorAll('.comfy-missing')) {
        box.hidden = !missing;
    }
    $('btn-server').hidden = missing;
    $('btn-log').hidden = missing;
    $('server').className = stateName;
    const label = { on: '실행 중', off: '꺼짐', starting: '시작 중… (첫 기동은 오래 걸릴 수 있음)' }[stateName];
    $('server-text').textContent = 'on' === stateName ? `${label} · ${url}${owned ? '' : ' (외부)'}` : label;
    $('btn-server').textContent = 'on' === stateName ? '중지' : '시작';
    $('btn-server').disabled = 'starting' === stateName || ('on' === stateName && !owned);
    $('btn-server').title = 'on' === stateName && !owned ? '이 앱이 띄운 서버가 아니라 끄지 않습니다' : '';
    $('btn-comfy').disabled = !state.serverOn;
    updateRunButton();
}

$('btn-server').onclick = () => send({ t: state.serverOn ? 'server-stop' : 'server-start' });
$('btn-comfy').onclick = () => send({ t: 'open-comfy' });
$('btn-log').onclick = () => send({ t: 'open-folder', which: 'log' });

// ---------- 워크플로 목록 (작업대) ----------
function renderWorkflowList(items, select) {
    const list = $('wf-select');
    list.innerHTML = '';
    $('wf-empty').hidden = items.length > 0;
    for (const name of items) {
        const option = document.createElement('option');
        option.value = name;
        option.textContent = name;
        list.appendChild(option);
    }
    list.value = state.workflow?.name || '';
    const wanted = select || storageGet('gm.lastWorkflow', null);
    if (!state.workflow && wanted && items.includes(wanted)) {
        send({ t: 'wf-load', name: wanted });
    }
}

$('btn-import').onclick = () => send({ t: 'wf-import' });
$('btn-wf-folder').onclick = () => send({ t: 'open-folder', which: 'workflows' });
$('wf-select').onchange = () => send({ t: 'wf-load', name: $('wf-select').value });
// 작업대 = 워크플로·입력값·직접 생성. 사장님 화면에선 기본 숨김 (Claude 가 대신 돌림)
$('btn-bench').onclick = () => storageSet('gm.bench', document.body.classList.toggle('bench'));
document.body.classList.toggle('bench', true === storageGet('gm.bench', false));

// ---------- 입력 폼 ----------
function valuesKey(name) {
    return `gm.values.${name}`;
}

function loadWorkflow(workflow) {
    state.workflow = workflow;
    state.values = storageGet(valuesKey(workflow.name), {});
    state.randomSeed = storageGet(`gm.random.${workflow.name}`, {});
    storageSet('gm.lastWorkflow', workflow.name);
    $('wf-select').value = workflow.name;
    renderForm();
    updateRunButton();
}

function currentValue(nodeId, field) {
    const saved = state.values[nodeId];
    return saved && field.key in saved ? saved[field.key] : field.value;
}

function setValue(nodeId, field, value) {
    const node = state.values[nodeId] || (state.values[nodeId] = {});
    if (value === field.value) {
        delete node[field.key];
        if (0 === Object.keys(node).length) {
            delete state.values[nodeId];
        }
    } else {
        node[field.key] = value;
    }
    storageSet(valuesKey(state.workflow.name), state.values);
}

function renderForm() {
    const form = $('form');
    form.innerHTML = '';
    if (!state.workflow) {
        return;
    }
    const showAll = $('show-all').checked;
    // _meta.gm = "main" 표시가 있는 워크플로면 기본은 그 노드만
    const hasMain = state.workflow.nodes.some((node) => node.main);
    let shown = 0;
    for (const node of state.workflow.nodes) {
        if (!showAll && (0 === node.fields.length || (hasMain && !node.main))) {
            continue;
        }
        ++shown;
        const box = document.createElement('div');
        box.className = 'node' + (0 === node.fields.length ? ' empty' : '');
        const head = document.createElement('div');
        head.className = 'node-head';
        head.innerHTML = `<b></b><small></small>`;
        head.querySelector('b').textContent = node.title;
        head.querySelector('small').textContent = `#${node.id} ${node.title === node.classType ? '' : node.classType}`;
        const body = document.createElement('div');
        body.className = 'node-body';
        if (0 === node.fields.length) {
            body.textContent = '편집할 값 없음 (링크만)';
        }
        for (const field of node.fields) {
            body.appendChild(renderField(node, field));
        }
        box.append(head, body);
        form.appendChild(box);
    }
    if (0 === shown) {
        form.innerHTML = '<p class="hint">편집할 값이 있는 노드가 없습니다. "전체 노드" 를 켜면 모든 노드를 봅니다.</p>';
    }
}

function renderField(node, field) {
    const wrap = document.createElement('div');
    wrap.className = 'field';
    const label = document.createElement('label');
    const name = document.createElement('span');
    name.textContent = field.key;
    const mark = document.createElement('span');
    label.append(name, mark);
    wrap.appendChild(label);

    const refreshMark = () => {
        const changed = currentValue(node.id, field) !== field.value;
        mark.textContent = changed ? '변경됨' : '';
        mark.className = changed ? 'changed' : '';
    };
    refreshMark();
    const value = currentValue(node.id, field);

    if ('image' === field.kind) {
        const pick = document.createElement('div');
        pick.className = 'pick';
        const img = document.createElement('img');
        img.hidden = true;
        const text = document.createElement('span');
        text.textContent = value;
        const button = document.createElement('button');
        button.textContent = '이미지 선택';
        button.onclick = () => {
            if (!state.serverOn) {
                toast('ComfyUI 서버가 꺼져 있습니다. 먼저 시작하세요.');
                return;
            }
            send({ t: 'pick-image', node: node.id, key: field.key });
        };
        pick.append(img, text, button);
        pick.dataset.node = node.id;
        pick.dataset.key = field.key;
        pick.onpicked = (picked) => {
            setValue(node.id, field, picked.value);
            text.textContent = picked.value;
            img.src = picked.url;
            img.hidden = false;
            refreshMark();
        };
        wrap.appendChild(pick);
        return wrap;
    }

    if ('bool' === field.kind) {
        const check = document.createElement('input');
        check.type = 'checkbox';
        check.checked = !!value;
        check.onchange = () => { setValue(node.id, field, check.checked); refreshMark(); };
        label.prepend(check);
        return wrap;
    }

    if ('number' === field.kind) {
        const line = document.createElement('div');
        line.className = 'inline';
        const input = document.createElement('input');
        input.type = 'number';
        input.step = 'any';
        input.value = value;
        input.onchange = () => {
            const parsed = Number(input.value);
            setValue(node.id, field, Number.isFinite(parsed) ? parsed : field.value);
            refreshMark();
        };
        line.appendChild(input);
        if (SEED_KEYS.includes(field.key)) {
            const seedKey = `${node.id}.${field.key}`;
            const random = document.createElement('label');
            random.className = 'check';
            random.innerHTML = '<input type="checkbox"> 매번 랜덤';
            const box = random.querySelector('input');
            box.checked = !!state.randomSeed[seedKey];
            box.onchange = () => {
                state.randomSeed[seedKey] = box.checked;
                storageSet(`gm.random.${state.workflow.name}`, state.randomSeed);
            };
            line.appendChild(random);
            input.dataset.seed = seedKey;
        }
        wrap.appendChild(line);
        return wrap;
    }

    const multiline = MULTILINE_KEYS.some((key) => field.key.toLowerCase().includes(key)) || String(value).length > 60;
    const input = document.createElement(multiline ? 'textarea' : 'input');
    if (!multiline) {
        input.type = 'text';
    }
    input.value = value;
    input.oninput = () => { setValue(node.id, field, input.value); refreshMark(); };
    wrap.appendChild(input);
    return wrap;
}

$('show-all').onchange = renderForm;
$('btn-reset').onclick = () => {
    if (!state.workflow) {
        return;
    }
    state.values = {};
    storageSet(valuesKey(state.workflow.name), state.values);
    renderForm();
};

function onImagePicked(message) {
    for (const pick of document.querySelectorAll('.pick')) {
        if (pick.dataset.node === message.node && pick.dataset.key === message.key) {
            pick.onpicked(message);
        }
    }
}

// ---------- 실행 ----------
function updateRunButton() {
    $('btn-run').disabled = !state.workflow || !state.serverOn || state.running;
    $('btn-cancel').hidden = !state.running;
}

function setProgress(text, value, max) {
    const bar = $('progress');
    bar.classList.toggle('busy', state.running && !(max > 0));
    $('progress-fill').style.width = max > 0 ? `${Math.round(value / max * 100)}%` : (state.running ? '' : '0');
    $('progress-text').textContent = text;
}

$('btn-run').onclick = () => {
    const values = structuredClone(state.values);
    // "매번 랜덤" seed 는 실행 때만 새 값 (저장값은 그대로)
    for (const [seedKey, on] of Object.entries(state.randomSeed)) {
        if (!on) {
            continue;
        }
        const [nodeId, key] = seedKey.split('.');
        (values[nodeId] || (values[nodeId] = {}))[key] = Math.floor(Math.random() * 2 ** 48);
    }
    send({ t: 'run', name: state.workflow.name, values });
};
$('btn-cancel').onclick = () => send({ t: 'cancel' });

let runStarted = 0;
let elapsedTimer = 0;
function onRunStart(workflow, external) {
    // 채팅이 시킨 실행이면 폼도 그 워크플로로 (GPT 그림처럼 워크플로가 아닌 실행 = external 은 폼을 그대로)
    if (!external && workflow && workflow !== state.workflow?.name) {
        send({ t: 'wf-load', name: workflow });
    }
    state.running = true;
    runStarted = Date.now();
    updateRunButton();
    setProgress(`${workflow} 시작`, 0, 0);
    clearInterval(elapsedTimer);
    elapsedTimer = setInterval(() => {
        $('progress-text').dataset.elapsed = `${Math.round((Date.now() - runStarted) / 1000)}s`;
    }, 1000);
}

function onRunEnd(text) {
    state.running = false;
    clearInterval(elapsedTimer);
    updateRunButton();
    setProgress(text, 0, 0);
}

// ---------- 결과 ----------
function runLabel(run) {
    const [date, time] = run.id.split('_');
    return `${date.slice(4, 6)}/${date.slice(6, 8)} ${time.slice(0, 2)}:${time.slice(2, 4)}`;
}

// 최근 결과물 갤러리 (리소스 칸 왼쪽) — 실행 하나 = 썸네일 하나 (첫 이미지, 없으면 확장자)
function renderHistory() {
    const gallery = $('gallery');
    gallery.innerHTML = '';
    for (const run of state.runs) {
        const shot = document.createElement('div');
        shot.className = 'shot';
        const image = run.files.find((file) => IMAGE_EXTENSIONS.includes(file.ext));
        if (image) {
            const img = document.createElement('img');
            img.src = image.url;
            img.loading = 'lazy';
            shot.appendChild(img);
        } else {
            const icon = document.createElement('div');
            icon.className = 'icon';
            icon.textContent = (run.files[0]?.ext || '').replace('.', '').toUpperCase() || '—';
            shot.appendChild(icon);
        }
        const label = document.createElement('div');
        label.textContent = runLabel(run);
        shot.appendChild(label);
        shot.title = `${run.workflow || run.id} · ${runLabel(run)} · ${run.files.length}개`;
        shot.classList.toggle('sel', state.currentRun?.id === run.id);
        shot.onclick = () => showRun(run);
        gallery.appendChild(shot);
    }
    if (0 === state.runs.length) {
        gallery.innerHTML = '<p class="hint">아직 결과물이 없습니다</p>';
    }
}

function showRun(run) {
    state.currentRun = run;
    renderHistory();
    $('run-title').textContent = `${run.workflow || run.id} · ${runLabel(run)}${run.seconds ? ` · ${Math.round(run.seconds)}초` : ''}`;
    $('btn-run-folder').hidden = false;
    const files = $('files');
    files.innerHTML = '';
    const tiles = [];
    for (const file of run.files) {
        const tile = document.createElement('div');
        tile.className = 'file';
        tile.title = file.name;
        if (IMAGE_EXTENSIONS.includes(file.ext)) {
            const img = document.createElement('img');
            img.src = file.url;
            tile.appendChild(img);
        } else {
            const icon = document.createElement('div');
            icon.className = 'icon';
            icon.textContent = file.ext.replace('.', '').toUpperCase() || 'FILE';
            tile.appendChild(icon);
        }
        const name = document.createElement('div');
        name.textContent = file.name;
        tile.appendChild(name);
        tile.onclick = () => {
            for (const other of tiles) {
                other.classList.toggle('sel', other === tile);
            }
            showFile(file);
        };
        tiles.push(tile);
        files.appendChild(tile);
    }
    // 3D 결과가 있으면 그걸 먼저, 없으면 첫 파일
    const first = run.files.findIndex((file) => MODEL_EXTENSIONS.includes(file.ext));
    const index = first >= 0 ? first : 0;
    if (tiles[index]) {
        tiles[index].click();
    } else {
        showNothing('결과 파일이 없습니다 (출력 노드가 파일을 남기지 않음)');
    }
}

// 소리는 파일을 통째로 받아 blob 주소로 재생 — 맥 WKWebView 는 사용자 스킴(app://)으로 audio 를 직접 못 여는 일이 있어서 (2026-11-20 Photino)
let audioBlobUrl = '';
let audioRequest = 0;

function hideAudio() {
    const audio = $('audio');
    audio.pause();
    audio.hidden = true;
    ++audioRequest;
    if (audioBlobUrl) {
        audio.removeAttribute('src');
        URL.revokeObjectURL(audioBlobUrl);
        audioBlobUrl = '';
    }
}

async function playAudio(url) {
    const request = ++audioRequest;
    const response = await fetch(url);
    const blob = await response.blob();
    if (request !== audioRequest) {
        return; // 그 사이 다른 파일을 고름
    }
    audioBlobUrl = URL.createObjectURL(blob);
    $('audio').src = audioBlobUrl;
    $('audio').hidden = false;
}

function showNothing(text) {
    viewer.clear();
    hideAudio();
    $('image').hidden = true;
    $('view-tools').hidden = true;
    $('view-empty').hidden = false;
    $('view-empty').textContent = text;
}

async function showFile(file) {
    hideAudio();
    if (AUDIO_EXTENSIONS.includes(file.ext)) {
        viewer.clear();
        $('image').hidden = true;
        $('view-tools').hidden = true;
        $('view-empty').hidden = true;
        try {
            await playAudio(file.url);
        } catch (error) {
            showNothing(`불러오기 실패: ${error.message || error}`);
        }
        return;
    }
    if (IMAGE_EXTENSIONS.includes(file.ext)) {
        viewer.clear();
        $('image').src = file.url;
        $('image').hidden = false;
        $('view-tools').hidden = true;
        $('view-empty').hidden = true;
        return;
    }
    if (!MODEL_EXTENSIONS.includes(file.ext)) {
        showNothing(`${file.ext} 미리보기는 지원하지 않습니다 — 폴더 열기로 확인하세요`);
        return;
    }
    $('image').hidden = true;
    $('view-empty').hidden = false;
    $('view-empty').textContent = '불러오는 중…';
    try {
        const info = await viewer.load(file.url, file.ext);
        $('view-empty').hidden = true;
        $('view-tools').hidden = false;
        $('mesh-info').textContent = `삼각형 ${info.triangles.toLocaleString()} · 정점 ${info.vertices.toLocaleString()}`;
        // 클립이 여러 개면 고르기
        const select = $('clip-select');
        select.innerHTML = '';
        for (const [index, name] of info.clipNames.entries()) {
            const option = document.createElement('option');
            option.value = index;
            option.textContent = name;
            select.appendChild(option);
        }
        select.hidden = 0 === info.clipNames.length;
    } catch (error) {
        showNothing(`불러오기 실패: ${error.message || error}`);
    }
}

$('btn-out-folder').onclick = () => send({ t: 'open-folder', which: 'outputs' });
$('btn-run-folder').onclick = () => send({ t: 'open-folder', which: 'run', run: state.currentRun?.id });
$('btn-spin').onclick = () => $('btn-spin').classList.toggle('on', viewer.toggleSpin());
$('btn-wire').onclick = () => $('btn-wire').classList.toggle('on', viewer.toggleWire());
$('btn-fit').onclick = () => viewer.fit();
$('clip-select').onchange = () => viewer.playClip(Number($('clip-select').value));

// ---------- 메시지 ----------
Host.on((message) => {
    switch (message.t) {
        case 'status': {
            setServer(message.state, message.url, message.owned, message.installed);
            break;
        }
        case 'wf-list': {
            renderWorkflowList(message.items, message.select);
            break;
        }
        case 'wf': {
            loadWorkflow(message);
            break;
        }
        case 'image-picked': {
            onImagePicked(message);
            break;
        }
        case 'history': {
            state.runs = message.runs;
            renderHistory();
            break;
        }
        case 'run-start': {
            onRunStart(message.workflow, message.external);
            break;
        }
        case 'progress': {
            const elapsed = $('progress-text').dataset.elapsed || '';
            setProgress(`${message.text}${message.max > 0 ? ` ${message.value}/${message.max}` : ''} · ${elapsed}`, message.value, message.max);
            break;
        }
        case 'run-done': {
            onRunEnd(`완료 · ${Math.round(message.run.seconds)}초`);
            state.runs.unshift(message.run);
            showRun(message.run);
            break;
        }
        case 'run-fail': {
            onRunEnd('실패');
            toast(message.text);
            break;
        }
        case 'error': {
            toast(message.text);
            break;
        }
        case 'wf-changed': {
            // 터미널 claude 가 workflows/ 를 고침 → 지금 폼 다시 읽기 (입력값은 유지)
            window.gameMaker.reloadWorkflow();
            break;
        }
        case 'folders': {
            $('btn-wf-folder').title = message.workflows;
            $('btn-out-folder').title = message.data;
            break;
        }
    }
});

// 다른 모듈이 쓰는 창구
window.gameMaker = {
    currentWorkflow: () => state.workflow?.name,
    reloadWorkflow: () => {
        if (state.workflow) {
            send({ t: 'wf-load', name: state.workflow.name });
        }
    },
};

updateRunButton();
send({ t: 'ready' });
