// 맨 위 프로젝트 줄: 고르기 · [새 프로젝트] · [삭제]. C# = Views/MainWindow.Projects.cs (Services/ProjectStore.cs)
// 고르면 현황판·게임 칸·Claude 터미널이 그 프로젝트로 바뀐다 (터미널은 그 프로젝트 대화)
const $ = (id) => document.getElementById(id);

let current = null;
let items = [];
let noteTimer = 0;

function send(message) {
    Host.post(message);
}

function showNote(text) {
    $('project-note').textContent = text || '';
    clearTimeout(noteTimer);
    if (text) {
        noteTimer = setTimeout(() => { $('project-note').textContent = ''; }, 8000);
    }
}

function engineLabel(engine) {
    return 'web' === engine ? '웹' : 'Godot';
}

function render() {
    const select = $('project-select');
    select.innerHTML = '';
    if (0 === items.length || !current) {
        const none = document.createElement('option');
        none.value = '';
        none.textContent = '프로젝트 없음 — [새 프로젝트]';
        select.appendChild(none);
    }
    for (const item of items) {
        const option = document.createElement('option');
        option.value = item.id;
        option.textContent = `${item.name}  ·  ${engineLabel(item.engine)}`;
        select.appendChild(option);
    }
    select.value = current || '';
    $('btn-project-delete').disabled = !current;
}

function closeForm() {
    $('project-new-form').hidden = true;
    $('btn-project-new').hidden = false;
}

$('project-select').onchange = (event) => {
    const id = event.target.value;
    const name = items.find((item) => item.id === id)?.name;
    if (!id || id === current) {
        render();
        return;
    }
    if (window.confirm(`'${name}' 프로젝트로 바꿀까요?\n\n· 오른쪽 AI 가 그 프로젝트 대화로 다시 시작됩니다 (지금 대화는 끊깁니다)`)) {
        send({ t: 'project-select', id });
    } else {
        render();
    }
};

$('btn-project-new').onclick = () => {
    $('btn-project-new').hidden = true;
    $('project-new-form').hidden = false;
    $('project-name').focus();
};
$('btn-project-cancel').onclick = closeForm;
$('project-new-form').onsubmit = (event) => {
    event.preventDefault();
    const name = $('project-name').value.trim();
    if (!name) {
        $('project-name').focus();
        return;
    }
    send({ t: 'project-new', name, folder: $('project-folder').value.trim(), engine: $('project-engine').value });
    $('project-name').value = '';
    $('project-folder').value = '';
    $('project-engine').value = 'godot';
    closeForm();
};

// 삭제 = 확인 두 번 (게임 폴더·팀 문서 = 휴지통)
$('btn-project-delete').onclick = () => {
    const name = items.find((item) => item.id === current)?.name;
    if (!current) {
        return;
    }
    if (!window.confirm(`'${name}' 프로젝트를 삭제할까요?\n\n· 게임 폴더 · 팀 문서 → 휴지통 (안 되면 윈도우가 다시 묻습니다)\n· 그림 결과·워크플로는 그대로`)) {
        return;
    }
    if (window.confirm(`정말 '${name}' 을(를) 삭제합니다. 한 번 더 확인합니다.`)) {
        send({ t: 'project-delete', id: current });
    }
};

Host.on((message) => {
    if ('projects' === message.t) {
        items = message.items;
        current = message.current || null;
        render();
        if (message.note) {
            showNote(message.note);
        }
    }
});
