// GPU · VRAM 미터 — 맨 위 프로젝트 줄 오른쪽 끝. 값 = C# 이 3초마다(최소화 중엔 쉼) 'gpu' 로 보냄 (Views/MainWindow.Gpu.cs).
// 메시지가 한 번도 안 오면(맥·측정 실패) 미터는 숨긴 채로. 마우스를 올리면 그래픽카드 이름·정확한 값.
// 막대 색 = 사용량 미터와 같은 기준 (80% 노랑 · 95% 빨강, style.css .um-fill)
const $ = (id) => document.getElementById(id);

const WARN_PERCENT = 80;
const HOT_PERCENT = 95;
const GB = 1024 * 1024 * 1024;

function level(percent) {
    if (percent >= HOT_PERCENT) {
        return 'hot';
    }
    return percent >= WARN_PERCENT ? 'warn' : '';
}

function gb(bytes) {
    return (bytes / GB).toFixed(1);
}

function setBar(prefix, percent, text) {
    const clamped = Math.min(100, Math.max(0, percent));
    const fill = $(`${prefix}-fill`);
    fill.style.width = `${clamped}%`;
    fill.className = `um-fill ${level(clamped)}`.trim();
    const value = $(`${prefix}-value`);
    value.textContent = text;
    value.className = `um-value ${level(clamped)}`.trim();
}

function render(message) {
    const meter = $('gpu-meter');
    meter.hidden = false;
    const gpu = Math.round(message.percent);
    setBar('gpu', gpu, `${gpu}%`);

    const hasTotal = message.total > 0;
    const vramPercent = hasTotal ? (message.used / message.total) * 100 : 0;
    const vramText = hasTotal ? `${gb(message.used)}/${gb(message.total)} GB` : `${gb(message.used)} GB`;
    setBar('vram', vramPercent, vramText);

    const lines = [message.name || '그래픽카드 (이름 모름)'];
    lines.push(`GPU 사용률 ${gpu}% (작업 관리자와 같은 계산)`);
    lines.push(hasTotal
        ? `VRAM ${gb(message.used)} GB / ${gb(message.total)} GB (${Math.round(vramPercent)}%) — 이 PC 전체`
        : `VRAM ${gb(message.used)} GB 사용 — 이 PC 전체 (용량 모름)`);
    meter.title = lines.join('\n');
}

Host.on((message) => {
    if ('gpu' === message.t) {
        render(message);
    }
});
