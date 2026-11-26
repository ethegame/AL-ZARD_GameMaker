// 게임 진입점: 캔버스·입력·게임 루프. 규칙 계산은 DOM 없는 별도 모듈로 나눠 두면 node 로 테스트할 수 있다.
const canvas = document.getElementById('game');
const ctx = canvas.getContext('2d');

// 지금 눌린 키 (event.code 기준)
const keys = new Set();
window.addEventListener('keydown', (event) => keys.add(event.code));
window.addEventListener('keyup', (event) => keys.delete(event.code));

// 규칙 갱신 자리: dt = 지난 프레임 이후 초
function update(dt) {
}

// 그리기 자리
function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#ddd';
    ctx.font = '24px sans-serif';
    ctx.textAlign = 'center';
    ctx.fillText('새 게임', canvas.width / 2, canvas.height / 2);
}

// 게임 루프 (탭이 숨겨졌다 돌아와도 dt 가 튀지 않게 상한)
let last = performance.now();
function frame(now) {
    const dt = Math.min((now - last) / 1000, 0.1);
    last = now;
    update(dt);
    draw();
    requestAnimationFrame(frame);
}
requestAnimationFrame(frame);
