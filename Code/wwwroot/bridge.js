// C# ↔ 화면 통로 (Photino: Windows WebView2 · 맥 WKWebView). 다른 스크립트보다 먼저 불러온다.
// 출처 토큰 + Ctrl+F·G 막기 포함.
//   토큰: C# 이 시작마다 만든 난수(index.html?tok=)를 모든 메시지에 tok 로 붙임 → C# (Core/HostWindow.cs) 은 토큰 없는 메시지를 버림
//   Host.post({t, ...})       — C# 로 보내기 (JSON 글자 하나로)
//   Host.on(function (m) {})  — C# 에서 온 메시지 받기 (풀린 객체)
//   Host.os                   — 'win' | 'mac' (index.html?os=)
// 그리고 앱 화면이 바뀌지 않게 막는 것들: 바깥 링크 = 기본 브라우저, F5·Ctrl+R 새로고침 · 확대 · 파일 끌어다 놓기 · 기본 오른쪽 메뉴
(function () {
    'use strict';

    const params = new URLSearchParams(location.search);
    const os = params.get('os') === 'mac' ? 'mac' : 'win';
    const token = params.get('tok') || '';
    const listeners = [];
    const legacy = [];
    let rawSend = function () {};
    let rawListen = function () {};

    // 보내는 길 고르기: 미리보기 가짜(브라우저) → WebView2 원래 함수 → Photino window.external
    const stub = window.__hostStub;
    const wv = window.chrome && window.chrome.webview;
    if (stub) {
        rawSend = stub.send;
        rawListen = stub.listen;
    } else if (wv && typeof wv.postMessage === 'function') {
        const post = wv.postMessage.bind(wv);
        const add = wv.addEventListener.bind(wv);
        rawSend = function (text) { post(text); };
        rawListen = function (cb) { add('message', function (e) { cb(e.data); }); };
        // 옛 코드가 객체를 넣어도 글자로 (Photino 는 글자 메시지만 받음)
        try {
            wv.postMessage = function (msg) { Host.post(msg); };
            wv.addEventListener = function (type, cb, opt) {
                if (type === 'message') {
                    legacy.push(cb);
                    return;
                }
                add(type, cb, opt);
            };
        } catch (err) {
            // 바꿀 수 없는 물건이면 옛 코드만 못 씀 — 원본 화면은 Host 를 씀
        }
    } else if (window.external && typeof window.external.sendMessage === 'function') {
        rawSend = function (text) { window.external.sendMessage(text); };
        rawListen = function (cb) { window.external.receiveMessage(cb); };
        // 맥: 옛 코드용 chrome.webview 흉내
        window.chrome = window.chrome || {};
        try {
            window.chrome.webview = {
                postMessage: function (msg) { Host.post(msg); },
                addEventListener: function (type, cb) {
                    if (type === 'message') {
                        legacy.push(cb);
                    }
                },
                removeEventListener: function () {}
            };
        } catch (err) {
            // 무시
        }
    }

    function dispatch(raw) {
        let m = raw;
        if (typeof raw === 'string') {
            try {
                m = JSON.parse(raw);
            } catch (err) {
                return;
            }
        }
        if (!m || typeof m !== 'object') {
            return;
        }
        for (let i = 0; i < listeners.length; i++) {
            try {
                listeners[i](m);
            } catch (err) {
                report(err);
            }
        }
        for (let i = 0; i < legacy.length; i++) {
            try {
                legacy[i]({ data: m });
            } catch (err) {
                report(err);
            }
        }
    }

    function report(err) {
        try {
            rawSend(JSON.stringify({ t: 'jserror', d: String(err && err.stack ? err.stack : err), tok: token }));
        } catch (e2) {
            // 보낼 수도 없으면 그만
        }
    }

    const Host = {
        os: os,
        post: function (msg) {
            let obj = msg;
            if (typeof msg === 'string') {
                try {
                    obj = JSON.parse(msg);
                } catch (err) {
                    return;
                }
            }
            if (!obj || typeof obj !== 'object') {
                return;
            }
            rawSend(JSON.stringify(Object.assign({}, obj, { tok: token })));
        },
        on: function (cb) {
            listeners.push(cb);
        }
    };
    window.Host = Host;
    document.documentElement.classList.add('os-' + os);
    rawListen(dispatch);

    window.addEventListener('error', function (e) {
        report((e.message || 'error') + ' @ ' + (e.filename || '') + ':' + (e.lineno || 0));
    });

    // ---------------- 앱 화면 지키기 ----------------

    function isOwnUrl(href) {
        try {
            const u = new URL(href, location.href);
            return u.origin === location.origin;
        } catch (err) {
            return true;
        }
    }

    // 바깥 링크 = 기본 브라우저 (앱 화면이 그 페이지로 바뀌지 않게)
    document.addEventListener('click', function (e) {
        const a = e.target.closest ? e.target.closest('a[href]') : null;
        if (!a) {
            return;
        }
        const href = a.getAttribute('href');
        if (!href || href.charAt(0) === '#' || href.indexOf('javascript:') === 0) {
            return;
        }
        if (isOwnUrl(a.href)) {
            return;
        }
        e.preventDefault();
        Host.post({ t: 'openurl', d: a.href });
    }, true);

    window.open = function (url) {
        if (url) {
            Host.post({ t: 'openurl', d: String(new URL(url, location.href)) });
        }
        return null;
    };

    // 새로고침·인쇄·확대 막기 (터미널이 날아가지 않게). stopPropagation 은 안 함 — 터미널이 Ctrl+R 등을 그대로 받게
    document.addEventListener('keydown', function (e) {
        const mod = e.ctrlKey || e.metaKey;
        if (e.key === 'F5' || e.key === 'F12') {
            e.preventDefault();
            return;
        }
        if (!mod) {
            return;
        }
        const k = (e.key || '').toLowerCase();
        if (k === 'r' || k === 'p' || k === '+' || k === '-' || k === '=' || k === '0' || k === 'f' || k === 'g') {
            e.preventDefault();
        }
    }, true);
    window.addEventListener('wheel', function (e) {
        if (e.ctrlKey) {
            e.preventDefault();
        }
    }, { passive: false });

    // 파일을 끌어다 놓으면 그 파일로 화면이 바뀌는 것 막기
    window.addEventListener('dragover', function (e) { e.preventDefault(); });
    window.addEventListener('drop', function (e) { e.preventDefault(); });

    // 기본 오른쪽 메뉴(새로고침·뒤로 등) 대신 — 입력칸만 그대로 (복사·붙여넣기)
    document.addEventListener('contextmenu', function (e) {
        const t = e.target;
        if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) {
            return;
        }
        e.preventDefault();
    });
})();
