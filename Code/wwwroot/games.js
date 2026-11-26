// 프로그래밍 칸의 "플레이할 게임" (지금 프로젝트 게임 폴더의 Godot 프로젝트 또는 웹 게임). C# = Views/MainWindow.Dev.cs
const $ = (id) => document.getElementById(id);

function send(message) {
    Host.post(message);
}

function renderGames(items, hasGodot, emptyText, engine, engineName) {
    const isWeb = 'web' === engine;
    // 프로그래밍 칸 머리에 엔진 표시 (프로젝트 없으면 비움)
    $('game-engine').textContent = engine ? `엔진: ${engineName}` : '';
    const list = $('game-list');
    list.innerHTML = '';
    $('game-empty').hidden = items.length > 0;
    if (emptyText) {
        $('game-empty').textContent = emptyText;
    }
    for (const game of items) {
        const li = document.createElement('li');
        const name = document.createElement('span');
        name.className = 'name';
        name.textContent = game.name;
        name.title = game.path;
        li.appendChild(name);
        // 웹 = 에디터 없음 ([▶] 는 기본 브라우저로 열기), Godot 실행 파일이 없어도 켜짐
        const buttons = isWeb
            ? [
                ['▶ 플레이', 'game-run', '기본 브라우저로 열기 (게임 폴더를 이 PC 안에서만 잠깐 내줌)', 'play'],
                ['폴더', 'game-folder', '게임 폴더 열기', ''],
            ]
            : [
                ['▶ 플레이', 'game-run', '게임 실행', 'play'],
                ['에디터', 'game-edit', 'Godot 에디터로 열기', ''],
                ['폴더', 'game-folder', '프로젝트 폴더 열기', ''],
            ];
        for (const [label, t, title, cls] of buttons) {
            const button = document.createElement('button');
            button.textContent = label;
            button.title = title;
            button.className = cls;
            button.disabled = !isWeb && 'game-folder' !== t && !hasGodot;
            button.onclick = () => send({ t, path: game.path });
            li.appendChild(button);
        }
        list.appendChild(li);
    }
}

Host.on((message) => {
    switch (message.t) {
        case 'games': {
            renderGames(message.items, message.godot, message.empty, message.engine, message.engineName);
            break;
        }
    }
});
