/**
 * Traynexus WebView2 Bridge
 * 封装 window.chrome.webview.hostObjects.traynexus 的调用，
 * 提供基于 Promise 的异步 API，并处理 hostObject 同步代理的细节。
 *
 * 用法：
 *   const mem = await TraynexusBridge.getMemorySnapshot();
 *   const ok = await TraynexusBridge.updateSettings(3, true, 80);
 */
(function (global) {
    'use strict';

    // hostObjects.traynexus 是同步代理对象，调用方法返回的是同步结果。
    // 但 hostObject 上的 string 返回值会通过 .toString() 取回。
    // 对于返回 JSON 字符串的方法，我们直接拿字符串再 JSON.parse。
    // 对于 bool/void 方法，直接返回。
    function getHost() {
        try {
            return global.chrome.webview.hostObjects.traynexus;
        } catch (e) {
            console.error('[TraynexusBridge] host object 不可用:', e);
            return null;
        }
    }

    /**
     * 调用返回 JSON 字符串的方法并解析为对象。
     */
    function callJson(methodName, args) {
        return new Promise(function (resolve, reject) {
            try {
                var host = getHost();
                if (!host) { reject(new Error('host object 不可用')); return; }
                var fn = host[methodName];
                if (typeof fn !== 'function') {
                    reject(new Error('方法不存在: ' + methodName));
                    return;
                }
                var raw = fn.apply(host, args || []);
                // hostObject 方法返回 string 时是 ByReference，需 .toString() / .then
                // 不同 WebView2 版本表现略有差异，统一做容错
                Promise.resolve(raw)
                    .then(function (v) {
                        if (v == null) { resolve(null); return; }
                        var str = typeof v === 'string' ? v : String(v);
                        try { resolve(JSON.parse(str)); }
                        catch (e) {
                            console.error('[TraynexusBridge] JSON.parse 失败:', methodName, str, e);
                            resolve({ __raw: str });
                        }
                    })
                    .catch(function (e) {
                        console.error('[TraynexusBridge] 调用失败:', methodName, e);
                        reject(e);
                    });
            } catch (e) {
                console.error('[TraynexusBridge] 异常:', methodName, e);
                reject(e);
            }
        });
    }

    /**
     * 调用返回 bool 的方法。
     */
    function callBool(methodName, args) {
        return new Promise(function (resolve, reject) {
            try {
                var host = getHost();
                if (!host) { reject(new Error('host object 不可用')); return; }
                var fn = host[methodName];
                if (typeof fn !== 'function') {
                    reject(new Error('方法不存在: ' + methodName));
                    return;
                }
                var raw = fn.apply(host, args || []);
                Promise.resolve(raw)
                    .then(function (v) {
                        resolve(v === true || v === 'true' || v === 1);
                    })
                    .catch(function (e) { reject(e); });
            } catch (e) { reject(e); }
        });
    }

    /**
     * 调用无返回值方法。
     */
    function callVoid(methodName, args) {
        return new Promise(function (resolve, reject) {
            try {
                var host = getHost();
                if (!host) { reject(new Error('host object 不可用')); return; }
                var fn = host[methodName];
                if (typeof fn !== 'function') {
                    reject(new Error('方法不存在: ' + methodName));
                    return;
                }
                Promise.resolve(fn.apply(host, args || []))
                    .then(function () { resolve(); })
                    .catch(function (e) { reject(e); });
            } catch (e) { reject(e); }
        });
    }

    /**
     * 调用返回普通字符串（非 JSON）的方法。
     */
    function callString(methodName, args) {
        return new Promise(function (resolve, reject) {
            try {
                var host = getHost();
                if (!host) { reject(new Error('host object 不可用')); return; }
                var fn = host[methodName];
                if (typeof fn !== 'function') {
                    reject(new Error('方法不存在: ' + methodName));
                    return;
                }
                var raw = fn.apply(host, args || []);
                Promise.resolve(raw)
                    .then(function (v) {
                        if (v == null) { resolve(''); return; }
                        resolve(typeof v === 'string' ? v : String(v));
                    })
                    .catch(function (e) { reject(e); });
            } catch (e) { reject(e); }
        });
    }

    var Bridge = {
        // 内存 & 释放
        getMemorySnapshot: function () { return callJson('GetMemorySnapshot', []); },
        getBatteryInfo: function () { return callJson('GetBatteryInfo', []); },
        executeRelease: function () { return callJson('ExecuteRelease', []); },
        previewTargets: function () { return callJson('PreviewTargets', []); },
        getChargeCapability: function () { return callJson('GetChargeCapability', []); },
        setChargeLimit: function (percent) { return callJson('SetChargeLimit', [percent]); },

        // 设置
        getSettings: function () { return callJson('GetSettings', []); },
        updateSettings: function (mode, thresholdEnabled, thresholdPercent) {
            return callJson('UpdateSettings', [mode, thresholdEnabled, thresholdPercent]);
        },
        updateChargeSettings: function (chargeMode, chargeLimit) {
            return callJson('UpdateChargeSettings', [chargeMode, chargeLimit]);
        },

        // 白名单
        getWhitelistContent: function () { return callString('GetWhitelistContent', []); },
        saveWhitelist: function (names) { return callBool('SaveWhitelist', [names]); },

        // 文件夹/文件
        openConfigFolder: function () { return callVoid('OpenConfigFolder', []); },
        openWhitelistInNotepad: function () { return callVoid('OpenWhitelistInNotepad', []); },

        // 自启动
        getAutoStartState: function () { return callBool('GetAutoStartState', []); },
        setAutoStart: function (enable) { return callBool('SetAutoStart', [enable]); },

        // URL & 迁移
        openUrl: function (url) { return callVoid('OpenUrl', [url]); },
        checkMigration: function () { return callJson('CheckMigration', []); },

        // 窗口控制（控制台标题栏按钮）
        minimizeWindow: function () { return callVoid('MinimizeWindow', []); },
        maximizeWindow: function () { return callVoid('MaximizeWindow', []); },
        closeWindow: function () { return callVoid('CloseWindow', []); },

        // 托盘菜单动作
        openConsole: function (navTarget) { return callVoid('OpenConsole', [navTarget || '']); },
        exitApp: function () { return callVoid('ExitApp', []); },

        // 接收来自 C# 的消息
        // 用法：Bridge.onMessage(function (data) { ... })
        onMessage: function (handler) {
            try {
                global.chrome.webview.addEventListener('message', function (e) {
                    try {
                        var data = e.data;
                        if (typeof data === 'string') {
                            try { data = JSON.parse(data); } catch (_) {}
                        }
                        handler(data, e);
                    } catch (err) {
                        console.error('[TraynexusBridge] onMessage handler 异常:', err);
                    }
                });
            } catch (e) {
                console.error('[TraynexusBridge] 注册 message 监听失败:', e);
            }
        }
    };

    global.TraynexusBridge = Bridge;
})(typeof window !== 'undefined' ? window : this);
