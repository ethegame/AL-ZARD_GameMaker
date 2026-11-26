// 그림 도구(ComfyUI) 설치 — 리소스 칸·작업대의 [설치 방법] (index.html #comfy-guide).
// [설치하기] = C# (Views/MainWindow.Comfy.cs → Services/ComfyInstaller.cs) 이 설치 폴더\ComfyUI 에 받아 풀고 저장·서버 시작.
// 이미 가진 사람 = 접힌 칸에서 폴더를 정하면 C# (Views/MainWindow.cs SetComfyRootAsync) 이 모양을 확인하고 %LOCALAPPDATA%\AL-ZARD_GAMEMAKER\settings.json 에 저장한 뒤 서버를 띄운다
const $ = (id) => document.getElementById(id);

let installing = false;
let plan = null; // 마지막 comfy-plan (C# ComfyInstaller.Plan)

function send(message) {
    Host.post(message);
}

function openGuide() {
    $('comfy-root-result').textContent = '';
    $('comfy-guide').hidden = false;
    if (!installing) {
        $('comfy-install-result').textContent = '';
        $('plan-gpu').textContent = '확인 중…';
        $('btn-comfy-install').disabled = true;
        send({ t: 'comfy-install-check' });
    }
}

function showPlan(next) {
    plan = next;
    const folder = next.folder || '—';
    $('plan-folder').textContent = folder;
    if (next.installed) {
        $('plan-gpu').textContent = '—';
        $('plan-asset').textContent = '이미 받아 둔 ComfyUI 가 있어요 — [설치하기] 를 누르면 바로 연결해요';
    } else {
        $('plan-gpu').textContent = next.gpu || '—';
        $('plan-asset').textContent = next.asset
            ? `${next.asset}${next.sizeMb ? ` (약 ${(next.sizeMb / 1024).toFixed(1)}GB)` : ''}${next.tag ? ` · ${next.tag}` : ''}`
            : '—';
    }
    $('plan-free').textContent = next.freeGb ? `${next.freeGb}GB` : '—';
    const note = next.error || next.warning || '';
    $('plan-warning').textContent = note;
    $('plan-warning').hidden = !note;
    $('plan-warning').classList.toggle('bad', !!next.error);
    $('btn-comfy-install').disabled = installing || !!next.error;
}

function setInstalling(on) {
    installing = on;
    $('btn-comfy-install').disabled = on || !!(plan && plan.error);
    $('btn-comfy-install-cancel').hidden = !on;
    $('btn-comfy-install-cancel').disabled = false;
    $('comfy-install-bar').hidden = !on;
    $('btn-comfy-save').disabled = on;
}

function showProgress(text, percent) {
    const bar = $('comfy-install-bar');
    bar.classList.toggle('busy', !(percent >= 0));
    $('comfy-install-fill').style.width = percent >= 0 ? `${Math.min(100, percent)}%` : '';
    $('comfy-install-text').textContent = text;
}

for (const button of document.querySelectorAll('.btn-comfy-guide')) {
    button.onclick = openGuide;
}
$('btn-guide-close').onclick = () => { $('comfy-guide').hidden = true; };
$('btn-comfy-install').onclick = () => {
    setInstalling(true);
    $('comfy-install-result').textContent = '';
    showProgress('확인 중…', -1);
    send({ t: 'comfy-install' });
};
$('btn-comfy-install-cancel').onclick = () => {
    $('btn-comfy-install-cancel').disabled = true;
    showProgress('취소하는 중…', -1);
    send({ t: 'comfy-install-cancel' });
};
$('btn-comfy-pick').onclick = () => send({ t: 'comfy-pick-root' });
$('btn-comfy-save').onclick = () => {
    const path = $('comfy-root').value.trim();
    if (!path) {
        $('comfy-root').focus();
        return;
    }
    $('comfy-root-result').textContent = '확인 중…';
    send({ t: 'comfy-set-root', path });
};

Host.on((message) => {
    switch (message.t) {
        case 'comfy-plan': {
            showPlan(message);
            break;
        }
        case 'comfy-install-progress': {
            if (!installing) {
                setInstalling(true);
            }
            showProgress(message.text, message.percent);
            break;
        }
        case 'comfy-install-done': {
            setInstalling(false);
            if (!message.ok) {
                $('comfy-install-result').textContent = message.text;
            }
            break;
        }
        case 'comfy-root-picked': {
            $('comfy-root').value = message.path;
            break;
        }
        case 'comfy-root-result': {
            // 자동 설치 끝(stay) = 그림 모델 안내를 읽도록 창을 열어 둠. 손으로 고른 폴더 = 잠시 뒤 닫음
            $(message.stay ? 'comfy-install-result' : 'comfy-root-result').textContent = message.text;
            if (message.ok && !message.stay) {
                setTimeout(() => { $('comfy-guide').hidden = true; }, 2500);
            }
            break;
        }
    }
});
