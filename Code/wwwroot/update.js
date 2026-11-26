// [⬆ 업데이트 <버전>] — 새 판이 있을 때만 [새 소식] 위에 보임. 확인·받기·설치 = C# (Services/AppUpdater.cs).
// 새 판을 깔고 처음 켜면 새 소식(about.js)이 저절로 열려서, 바뀐 점은 거기서 봄
const $ = (id) => document.getElementById(id);

function render(state) {
    const row = $('update-row');
    const button = $('btn-update');
    const note = $('update-note');
    const hasNote = 'string' === typeof state.note && state.note.length > 0;
    const hasUpdate = 'none' !== state.state;
    row.hidden = !(hasUpdate || hasNote);
    button.hidden = !hasUpdate;
    if (hasUpdate) {
        button.textContent = `⬆ 업데이트 ${state.version}`;
        button.disabled = 'busy' === state.state;
        button.title = `새 버전 ${state.version} 이 나왔어요 (지금 ${state.current}). 누르면 받아서 설치하고 다시 켜요 — 만든 게임·문서는 그대로예요.`;
    }
    note.hidden = !hasNote;
    note.textContent = hasNote ? state.note : '';
    note.classList.toggle('err', !!state.noteIsError);
}

$('btn-update').onclick = () => {
    const version = $('btn-update').textContent.replace('⬆ 업데이트 ', '');
    const ok = window.confirm(`AL-ZARD_GameMaker ${version} 로 업데이트할까요?\n\n· 새 버전을 받은 뒤 앱이 꺼지고, 설치가 끝나면 새 버전으로 다시 켜져요 (1~2분)\n· 만든 게임·문서·설정은 그대로예요\n· 열려 있던 Claude 대화는 다시 켜진 뒤 [이어 하기] 로 이어 갈 수 있어요`);
    if (ok) {
        Host.post({ t: 'update-start' });
    }
};

Host.on((message) => {
    if ('update-state' === message.t) {
        render(message);
    }
});
