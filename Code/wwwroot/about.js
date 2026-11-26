// 새 소식 = releasenotes.json. [새 소식] 버튼으로 열고, 버전이 오른 뒤 처음 켤 때 맨 앞 칸을 한 번 저절로 연다
const $ = (id) => document.getElementById(id);
const SEEN_KEY = 'mjg.seenVersion';

let notes = [];

function storageGet(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

function storageSet(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        // 저장 불가 환경이면 다음에 또 열릴 뿐
    }
}

function render() {
    const body = $('about-body');
    body.innerHTML = '';
    for (const note of notes) {
        const head = document.createElement('h3');
        head.textContent = `${note.version} · ${note.title || ''}`;
        const date = document.createElement('p');
        date.className = 'hint';
        date.textContent = note.date || '';
        const list = document.createElement('ul');
        for (const item of note.items || []) {
            const li = document.createElement('li');
            li.textContent = 'string' === typeof item ? item : item.text;
            list.appendChild(li);
        }
        body.append(head, date, list);
    }
    if (0 === notes.length) {
        body.innerHTML = '<p class="hint">소식이 없습니다.</p>';
    }
}

function open() {
    render();
    $('about').hidden = false;
    if (notes[0]) {
        storageSet(SEEN_KEY, notes[0].version);
    }
}

$('btn-about').onclick = open;
$('btn-about-close').onclick = () => { $('about').hidden = true; };

fetch('releasenotes.json')
    .then((response) => response.json())
    .then((data) => {
        notes = Array.isArray(data.notes) ? data.notes : [];
        if (notes[0] && storageGet(SEEN_KEY) !== notes[0].version) {
            open();
        }
    })
    .catch(() => {
        // 못 읽으면 버튼만 빈 소식
    });
