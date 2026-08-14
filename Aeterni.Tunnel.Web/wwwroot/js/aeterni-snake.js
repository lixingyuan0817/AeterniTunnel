// Aeterni Tunnel —— 背景贪吃蛇动画
// 固定长度蛇沿网格随机游走，随机吃豆（配合背景格子，视觉对齐）
(function () {
    const CELL = 28;                                  // 与背景网格一致
    const LEN = 12;                                   // 蛇固定长度
    const DIRS = [[1, 0], [0, 1], [-1, 0], [0, -1]];  // 右 下 左 上
    const TICK = 95;                                  // 步进间隔 ms

    let canvas = null, ctx = null, timer = null;
    let snake = [], food = null, dir = 0, flash = 0;

    function rand(n) { return Math.floor(Math.random() * n); }
    function wrap(v, n) { return ((v % n) + n) % n; }

    function cols() { return Math.floor(canvas.width / CELL); }
    function rows() { return Math.floor(canvas.height / CELL); }

    function init(id) {
        const el = document.getElementById(id);
        if (!el) return;
        if (timer) clearInterval(timer);              // 重复初始化保护
        canvas = el;
        ctx = canvas.getContext('2d');
        resize();
        window.addEventListener('resize', resize);

        // 初始：屏幕中部横排 12 节
        const sx = Math.floor(cols() / 2), sy = Math.floor(rows() / 2);
        snake = [];
        for (let i = 0; i < LEN; i++) snake.push({ x: sx - i, y: sy });
        dir = 0;
        spawnFood();
        timer = setInterval(step, TICK);
    }

    function resize() {
        if (!canvas) return;
        canvas.width = window.innerWidth;
        canvas.height = window.innerHeight;
    }

    function spawnFood() {
        // 在蛇头前方 4~13 格环形区域内随机生成 → 蛇很快吃到，节奏明显
        const head = snake[0];
        const dist = 4 + rand(10);
        const ang = Math.random() * Math.PI * 2;
        let fx = ((Math.round(head.x + Math.cos(ang) * dist)) % cols() + cols()) % cols();
        let fy = ((Math.round(head.y + Math.sin(ang) * dist)) % rows() + rows()) % rows();
        // 若与蛇身重叠，回退全图随机
        if (snake.some(s => s.x === fx && s.y === fy)) {
            do {
                fx = rand(cols()); fy = rand(rows());
            } while (snake.some(s => s.x === fx && s.y === fy));
        }
        food = { x: fx, y: fy };
    }

    // 贪吃蛇寻路：只允许 直行/左转/右转（不 180° 回头），
    // 排除会撞到身体的格子，再在安全方向里选最接近食物的方向
    function step() {
        const head = snake[0];
        const cands = [dir, (dir + 3) % 4, (dir + 1) % 4];
        const safe = [];
        for (const d of cands) {
            const tx = wrap(head.x + DIRS[d][0], cols());
            const ty = wrap(head.y + DIRS[d][1], rows());
            // 尾巴这步会移走，可踩；其余身体不可踩（贪吃蛇规则）
            if (!snake.slice(0, -1).some(s => s.x === tx && s.y === ty)) {
                safe.push({ d, dist: Math.abs(tx - food.x) + Math.abs(ty - food.y) });
            }
        }
        if (safe.length > 0) {
            safe.sort((a, b) => a.dist - b.dist);   // 等距时保持原序 → 优先直行
            dir = safe[0].d;
        }
        // safe 为空（理论死路）：保持原方向前进

        const nx = wrap(head.x + DIRS[dir][0], cols());
        const ny = wrap(head.y + DIRS[dir][1], rows());

        snake.unshift({ x: nx, y: ny });
        snake.pop();

        if (food && nx === food.x && ny === food.y) {
            flash = 5;      // 吃到反馈：蛇头闪亮几帧
            spawnFood();
        }
        draw();
    }

    function draw() {
        ctx.clearRect(0, 0, canvas.width, canvas.height);

        // 食物：品红亮点
        if (food) {
            ctx.fillStyle = '#f472b6';
            ctx.fillRect(food.x * CELL + 7, food.y * CELL + 7, CELL - 14, CELL - 14);
        }

        // 蛇：头部亮绿 → 尾部淡绿（像素方块）
        for (let i = snake.length - 1; i >= 0; i--) {
            const s = snake[i];
            if (i === 0) {
                // 吃到瞬间头部放大闪亮
                const pad = flash > 0 ? 0 : 2;
                ctx.fillStyle = flash > 0 ? '#6ee7b7' : '#4cd964';
                ctx.fillRect(s.x * CELL + pad, s.y * CELL + pad, CELL - pad * 2, CELL - pad * 2);
            } else {
                const t = 1 - i / snake.length;               // 0=尾 1=头
                ctx.fillStyle = `rgba(52, 199, 89, ${0.18 + 0.5 * t})`;
                ctx.fillRect(s.x * CELL + 4, s.y * CELL + 4, CELL - 8, CELL - 8);
            }
        }
        if (flash > 0) flash--;
    }

    window.aeterniSnake = { init };
})();
