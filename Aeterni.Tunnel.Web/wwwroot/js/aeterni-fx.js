// Aeterni Tunnel —— GSAP 页面动画（登录页示例）
// 依赖：window.gsap（wwwroot/js/vendor/gsap.min.js）
(function () {
    function init() {
        if (!window.gsap) return;

        const card = document.querySelector('.login-card');
        const toggle = document.querySelector('.theme-toggle');

        // 1) AETERNI 动画：纯 CSS 驱动（原版 box-1~7 / rL / middle2left keyframes），此处无需 GSAP

        // 2) TUNNEL 小字：逐个上浮淡入（与方块动画形成对比）
        const subLetters = document.querySelectorAll('.ats-sub-letter');
        if (subLetters.length) {
            gsap.fromTo(subLetters,
                { y: 14, opacity: 0 },
                {
                    y: 0, opacity: 1,
                    duration: .5,
                    ease: 'power2.out',
                    stagger: .05,
                    delay: 1.1,
                });
        }

        // 3) 登录卡弹性入场（back 缓动：过冲后回弹）
        if (card) {
            gsap.from(card, {
                y: 30,
                opacity: 0,
                scale: .94,
                duration: .9,
                ease: 'back.out(1.7)',
                delay: .45,
            });
            // 光晕脉冲
            gsap.fromTo(card,
                { boxShadow: '0 0 0 rgba(52,199,89,0)' },
                { boxShadow: '0 0 42px rgba(52,199,89,.18)', duration: 1.1, ease: 'sine.inOut', yoyo: true, repeat: -1, delay: 1.4 });
        }

        // 3) 主题切换按钮淡入
        if (toggle) {
            gsap.from(toggle, { opacity: 0, scale: .5, duration: .5, ease: 'back.out(2)', delay: .8 });
        }
        // 输入框聚焦由 Blazor 原生 FocusAsync 处理（更可靠），此处不再操作
    }

    // 大数字滚动（data-count 目标值，data-rate=1 保留一位小数）——首页与各管理页共用
    function countUp(delay) {
        document.querySelectorAll('[data-count]').forEach(el => {
            const target = parseFloat(el.dataset.count);
            const decimals = el.dataset.rate ? 1 : 0;
            const obj = { v: 0 };
            gsap.to(obj, {
                v: target, duration: 1.2, ease: 'power2.out', delay,
                onUpdate: () => {
                    el.textContent = decimals
                        ? obj.v.toFixed(1)
                        : Math.round(obj.v).toLocaleString('en-US');
                },
            });
        });
    }

    // 首页：数字滚动 + 卡片/日志 stagger 入场
    function initHome() {
        if (!window.gsap) return;

        // 顶栏淡入
        gsap.from('.top-bar', { y: -14, opacity: 0, duration: .5, ease: 'power2.out' });

        // 大数字滚动
        countUp(.35);

        // 性能仪表卡
        gsap.from('.stat-card', { y: 18, opacity: 0, duration: .6, ease: 'power2.out', stagger: .08, delay: .2 });

        // 隧道卡片墙（弹性入场）
        gsap.from('.proxy-card', { y: 24, opacity: 0, scale: .95, duration: .6, ease: 'back.out(1.4)', stagger: .07, delay: .5 });

        // 日志行
        gsap.from('.log-line', { x: -12, opacity: 0, duration: .4, ease: 'power1.out', stagger: .05, delay: .9 });

        // 全局流量折线图：面积渐变 + 终点发光点 + 入场动画 + 每秒实时追加
        const chart = document.querySelector('#traffic-chart');
        if (chart) {
            const upLine = chart.querySelector('.line-up');
            const downLine = chart.querySelector('.line-down');
            const areaUp = chart.querySelector('.area-up');
            const areaDown = chart.querySelector('.area-down');
            const dotUp = chart.querySelector('.dot-up');
            const dotDown = chart.querySelector('.dot-down');
            const haloUp = chart.querySelector('.dot-up-halo');
            const haloDown = chart.querySelector('.dot-down-halo');
            const cur = document.querySelector('#traffic-cur');
            const W = 800, H = 240, PAD = 10, RIGHT = 30, N = 40;
            const rnd = (a, b) => a + Math.random() * (b - a);
            const up = [], down = [];
            for (let i = 0; i < N; i++) { up.push(rnd(15, 90)); down.push(rnd(10, 60)); }
            const pt = (v, i) => `${(PAD + (i / (N - 1)) * (W - PAD - RIGHT)).toFixed(1)},${(H - PAD - (v / 100) * (H - PAD * 2)).toFixed(1)}`;
            const linePts = arr => arr.map(pt).join(' ');
            const areaD = arr => `M${arr.map(pt).join(' L ')} L ${W - RIGHT},${H - PAD} L ${PAD},${H - PAD} Z`;
            const setDot = (dot, halo, v) => {
                const x = W - RIGHT, y = H - PAD - (v / 100) * (H - PAD * 2);
                dot.setAttribute('cx', x); dot.setAttribute('cy', y);
                halo.setAttribute('cx', x); halo.setAttribute('cy', y);
            };
            const draw = () => {
                upLine.setAttribute('points', linePts(up));
                downLine.setAttribute('points', linePts(down));
                areaUp.setAttribute('d', areaD(up));
                areaDown.setAttribute('d', areaD(down));
                setDot(dotUp, haloUp, up[up.length - 1]);
                setDot(dotDown, haloDown, down[down.length - 1]);
            };
            draw();

            // 入场：面积淡入 → 折线画线 → 终点点弹入
            gsap.fromTo(areaUp, { opacity: 0 }, { opacity: 1, duration: .9, delay: .9 });
            gsap.fromTo(areaDown, { opacity: 0 }, { opacity: 1, duration: .9, delay: 1.1 });
            gsap.fromTo(upLine, { strokeDasharray: '3000', strokeDashoffset: 3000 },
                { strokeDashoffset: 0, duration: 1.4, ease: 'power2.inOut', delay: .8 });
            gsap.fromTo(downLine, { strokeDasharray: '3000', strokeDashoffset: 3000 },
                { strokeDashoffset: 0, duration: 1.4, ease: 'power2.inOut', delay: 1.0 });
            gsap.from([dotUp, dotDown], { scale: 0, transformOrigin: '50% 50%', duration: .45, ease: 'back.out(2.5)', delay: 1.4, stagger: .12 });

            // 实时模拟：每秒推进一个点
            setInterval(() => {
                up.push(rnd(15, 90)); up.shift();
                down.push(rnd(10, 60)); down.shift();
                draw();
                if (cur) {
                    cur.textContent = `↑${(up[up.length - 1] * 0.05).toFixed(1)} MB/s ↓${(down[down.length - 1] * 0.05).toFixed(1)} MB/s`;
                }
            }, 1000);
        }
    }

    // 客户端页：统计数字 + 树节点/详情面板入场
    function initClients() {
        if (!window.gsap) return;

        gsap.from('.top-bar', { y: -14, opacity: 0, duration: .5, ease: 'power2.out' });
        countUp(.3);
        gsap.from('.stat-card', { y: 18, opacity: 0, duration: .6, ease: 'power2.out', stagger: .07, delay: .2 });
        gsap.from('.tree-item', { x: -10, opacity: 0, duration: .45, ease: 'power1.out', stagger: .03, delay: .35 });
        gsap.from('.detail-panel', { y: 16, opacity: 0, duration: .5, ease: 'power2.out', delay: .5 });
    }

    // 设置页：设置卡片 stagger 入场
    function initSettings() {
        if (!window.gsap) return;

        gsap.from('.top-bar', { y: -14, opacity: 0, duration: .5, ease: 'power2.out' });
        gsap.from('.settings-card', { y: 20, opacity: 0, duration: .6, ease: 'power2.out', stagger: .1, delay: .2 });
    }

    window.aeterniFx = { init, initHome, initClients, initSettings };
})();
