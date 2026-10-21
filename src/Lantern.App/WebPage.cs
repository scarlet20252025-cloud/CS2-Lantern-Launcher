namespace Lantern.App;

/// <summary>
/// The page served at <c>/</c>. Kept as one self-contained string: no build
/// step, no CDN, no font download. It has to work on a friend's phone over a
/// forwarded port, where every extra request is another thing that can fail.
/// </summary>
static class WebPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="dark">
<title>CS2 Lantern · 房间控制台</title>
<style>
  :root{
    --bg:#0D1522; --card:#172233; --card2:#1B2A3D; --line:#29384D;
    --text:#E8ECF2; --muted:#93A5BC; --dim:#7A8CA4;
    --accent:#FFBF79; --amber:#F3AA5C; --green:#92BA9A; --red:#FFADAD; --blue:#365A70;
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);
       font:14px/1.5 "Microsoft YaHei UI","PingFang SC","Hiragino Sans GB",system-ui,sans-serif;
       -webkit-text-size-adjust:100%}
  a{color:var(--accent)}
  .wrap{max-width:1080px;margin:0 auto;padding:18px 16px 60px}
  header{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:16px}
  .logo{font-weight:700;letter-spacing:.28em;font-size:19px}
  .sub{color:var(--muted);font-size:12px}
  .pill{margin-left:auto;display:flex;gap:8px;align-items:center;font-size:12px;color:var(--muted)}
  .dot{width:8px;height:8px;border-radius:50%;background:var(--dim);display:inline-block}
  .dot.on{background:var(--green);box-shadow:0 0 8px #92BA9A88}
  .dot.busy{background:var(--amber)}
  .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:16px;margin-bottom:14px}
  h2{font-size:15px;margin:0 0 12px;font-weight:600;display:flex;align-items:center;gap:8px}
  h2 .hint{font-weight:400;font-size:11.5px;color:var(--dim);margin-left:auto}
  .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:10px}
  .stat{background:var(--card2);border-radius:9px;padding:11px 13px}
  .stat b{display:block;font-size:20px;font-weight:600;font-variant-numeric:tabular-nums}
  .stat span{color:var(--muted);font-size:11.5px}
  .mapname{font-family:Consolas,ui-monospace,monospace;color:var(--accent);word-break:break-all}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th{text-align:left;color:var(--dim);font-weight:500;font-size:11.5px;padding:4px 8px;border-bottom:1px solid var(--line)}
  td{padding:7px 8px;border-bottom:1px solid #202d40;font-variant-numeric:tabular-nums}
  tr:last-child td{border-bottom:none}
  .tag{font-size:10.5px;padding:1px 6px;border-radius:5px;border:1px solid var(--line);color:var(--muted)}
  .tag.bot{color:#9DC3E6;border-color:#2f4a63}
  .tag.ok{color:var(--green);border-color:#33523d}
  .tag.wait{color:var(--amber);border-color:#5a4527}
  .empty{color:var(--dim);font-size:12.5px;padding:8px 0}
  button{font:inherit;cursor:pointer;border-radius:8px;border:1px solid var(--line);
         background:#253246;color:var(--text);padding:9px 14px;transition:.12s}
  button:hover:not(:disabled){border-color:var(--amber)}
  button:disabled{opacity:.45;cursor:not-allowed}
  button.primary{background:var(--amber);color:#141C25;font-weight:600;border-color:transparent}
  button.warn{background:#3a2b22;color:#FFD09A;border-color:#5a4527}
  button.ghost{background:transparent}
  .maps{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:8px;max-height:340px;overflow:auto;padding-right:4px}
  .map{display:flex;flex-direction:column;gap:8px;background:var(--card2);border:1px solid var(--line);
       border-radius:9px;padding:12px 13px;text-align:left;min-height:118px}
  .map.cur{border-color:var(--amber);background:#302B26}
  .map .t{font-weight:600;font-size:13px;line-height:1.4;min-height:36px;
          display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
  .map .m{font-family:Consolas,monospace;font-size:11.5px;color:var(--accent);overflow-wrap:anywhere}
  .map .b{margin-top:auto;align-self:stretch;padding:7px 10px;font-size:12.5px}
  #log{background:#0A121D;border:1px solid var(--line);border-radius:9px;height:300px;overflow:auto;
       padding:10px 12px;font:12px/1.55 Consolas,ui-monospace,monospace;white-space:pre-wrap;word-break:break-word;color:#C6D3E4}
  #log .sys{color:var(--amber)}
  #log .err{color:var(--red)}
  #log .echo{color:#8FA6BF}
  .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
  .row input{flex:1 1 240px;min-width:0}
  input{font:inherit;background:#0F1A28;border:1px solid var(--line);border-radius:8px;
        color:var(--text);padding:10px 12px;width:100%}
  input:focus{outline:none;border-color:var(--amber)}
  .num{width:74px;flex:0 0 auto;text-align:center}
  .toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);background:#22303f;
         border:1px solid var(--line);border-radius:10px;padding:11px 18px;font-size:13px;
         max-width:min(90vw,560px);opacity:0;pointer-events:none;transition:.2s;z-index:9}
  .toast.show{opacity:1}
  .toast.bad{border-color:#6b3a3a;background:#2b1d1f;color:#FFD0D0}
  #gate{max-width:420px;margin:12vh auto 0}
  #gate h1{font-size:18px;margin:0 0 6px}
  #gate p{color:var(--muted);font-size:12.5px;margin:0 0 16px}
  .foot{color:var(--dim);font-size:11.5px;margin-top:18px;line-height:1.7}
</style>
</head>
<body>
<div class="wrap">
  <header>
    <div>
      <div class="logo">L A N T E R N</div>
      <div class="sub">CS2 房间控制台 · 只看不改进程</div>
    </div>
    <div class="pill"><span id="dot" class="dot"></span><span id="conn">正在连接…</span></div>
  </header>

  <div id="gate" class="card" hidden>
    <h1>需要口令</h1>
    <p>口令在房主启动器的「服务器」页里，一串小写字母和数字。房主重启启动器后口令不变；只有房主手动重置才会换。</p>
    <div class="row">
      <input id="token" placeholder="输入口令" autocomplete="off" spellcheck="false">
      <button class="primary" id="login">进入</button>
    </div>
  </div>

  <main id="app" hidden>
    <div class="card">
      <h2>服务器状态 <span class="hint" id="kindHint"></span></h2>
      <div class="stats">
        <div class="stat"><b id="sMap" class="mapname">—</b><span>当前地图</span></div>
        <div class="stat"><b id="sHumans">0</b><span>在线真人</span></div>
        <div class="stat"><b id="sBots">0</b><span>人机</span></div>
        <div class="stat"><b id="sPort">—</b><span>UDP 端口</span></div>
        <div class="stat"><b id="sCap">—</b><span>人数上限（启动参数）</span></div>
      </div>
      <div class="foot" id="addrLine"></div>
    </div>

    <div class="card">
      <h2>谁在房间里 <span class="hint">只有进入地图的人算“就绪”</span></h2>
      <table>
        <thead><tr><th style="width:64px">槽位</th><th>名字</th><th style="width:110px">状态</th><th style="width:64px">类型</th></tr></thead>
        <tbody id="players"></tbody>
      </table>
      <div class="empty" id="playersEmpty">还没有人。</div>
    </div>

    <div class="card">
      <h2>换地图 <span class="hint">换图会保留房间，但正在加载的玩家可能掉线</span></h2>
      <div class="row" style="margin-bottom:10px">
        <input id="mapFilter" placeholder="筛选地图名称或文件名…">
      </div>
      <div class="maps" id="maps"></div>
      <div class="empty" id="mapsEmpty">这台机器上没有可开的地图。</div>
    </div>

    <div class="card">
      <h2>人机与对局</h2>
      <div class="row" style="margin-bottom:10px">
        <button data-bot="add" data-team="t">加 T 人机</button>
        <button data-bot="add" data-team="ct">加 CT 人机</button>
        <input id="botCount" class="num" type="number" min="1" max="32" value="1" title="每次添加的数量">
        <button data-bot="remove" class="warn">清除所有人机</button>
      </div>
      <div class="row">
        <input id="balanceTotal" class="num" type="number" min="2" max="64" value="10" title="补位后总人数（含真人）">
        <button data-bot="balance">按总人数补位</button>
        <button data-bot="restart" class="ghost">重开对局</button>
      </div>
    </div>

    <div class="card">
      <h2>控制台 <span class="hint">命令直接下发到服务器，输出会回到这里</span></h2>
      <div id="log"></div>
      <div class="row" style="margin-top:10px">
        <input id="cmd" placeholder="输入 CS2 命令，例如 status" autocomplete="off" spellcheck="false">
        <button class="primary" id="send">发送</button>
        <button class="ghost" id="clearLog">清空显示</button>
      </div>
    </div>

    <div class="foot">
      这里不能启动或关闭服务器：停服会结束所有人的对局，只有坐在那台电脑前的人能决定。<br>
      换图、加人机、执行命令都会先读服务器真实状态再执行，失败会原样告诉你原因。
    </div>
  </main>
</div>
<div class="toast" id="toast"></div>

<script>
(function(){
  var $ = function(id){ return document.getElementById(id); };
  var seq = 0, timer = null, lastState = null, filter = "";

  function toast(msg, bad){
    var t = $("toast"); t.textContent = msg; t.className = "toast show" + (bad ? " bad" : "");
    clearTimeout(t._h); t._h = setTimeout(function(){ t.className = "toast"; }, 4200);
  }

  function api(path, body){
    return fetch(path, {
      method: body === undefined ? "GET" : "POST",
      headers: body === undefined ? {} : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: "same-origin"
    }).then(function(r){ return r.json(); });
  }

  function esc(s){
    return String(s).replace(/[&<>"']/g, function(c){
      return ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" })[c];
    });
  }

  function gate(show){
    $("gate").hidden = !show;
    $("app").hidden = show;
    if (show) { clearInterval(timer); timer = null; }
  }

  function logLine(text, cls){
    var box = $("log");
    var atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 40;
    var div = document.createElement("div");
    if (cls) div.className = cls;
    div.textContent = text;
    box.appendChild(div);
    while (box.childNodes.length > 900) box.removeChild(box.firstChild);
    if (atBottom) box.scrollTop = box.scrollHeight;
  }

  function renderMaps(maps){
    var host = $("maps"), shown = 0;
    host.textContent = "";
    maps.forEach(function(m){
      var hay = (m.title + " " + m.mapName).toLowerCase();
      if (filter && hay.indexOf(filter) < 0) return;
      shown++;
      var card = document.createElement("div");
      card.className = "map" + (m.current ? " cur" : "");
      card.innerHTML = '<div class="t">' + esc(m.title) + '</div><div class="m">' + esc(m.mapName) + '</div>';
      var b = document.createElement("button");
      b.className = "b" + (m.current ? " ghost" : " primary");
      b.textContent = m.current ? "当前地图" : "换到这张";
      b.disabled = m.current || (lastState && lastState.busy);
      b.onclick = function(){ switchMap(m); };
      card.appendChild(b);
      host.appendChild(card);
    });
    $("mapsEmpty").hidden = shown > 0;
  }

  function switchMap(m){
    if (!confirm("换到《" + m.title + "》？\n\n服务器会卸载当前地图。正在加载的玩家可能需要重连。")) return;
    busy(true);
    api("/api/map", { identity: m.identity }).then(function(r){
      busy(false);
      if (r.ok) { toast("已换到《" + r.map + "》"); tick(); }
      else toast(r.error, true);
    }).catch(function(e){ busy(false); toast("请求失败：" + e.message, true); });
  }

  function busy(on){
    document.querySelectorAll("button").forEach(function(b){
      if (b.dataset.keep !== "1") b.disabled = on || b._cur;
    });
  }

  function render(s){
    lastState = s;
    var sv = s.server || {};
    $("dot").className = "dot " + (s.busy ? "busy" : (sv.running ? "on" : ""));
    $("conn").textContent = s.busy ? "正在处理…" : (sv.running ? "服务器在线" : "服务器未加载地图");
    $("sMap").textContent = sv.map || "—";
    $("sHumans").textContent = sv.humans || 0;
    $("sBots").textContent = sv.bots || 0;
    $("sPort").textContent = sv.port || "—";
    $("sCap").textContent = sv.capacity == null ? "未知" : sv.capacity;
    $("kindHint").textContent = sv.running ? ("工坊 " + (sv.addon || "官方地图")) : "";
    $("addrLine").textContent = sv.running
      ? ("服务器身份 " + (sv.identity || "未提供") + " · UDP " + sv.port)
      : "独立服务器尚未加载地图；启动器里开图后这里会自动出现。";

    var rows = s.players || [];
    var tb = $("players"); tb.textContent = "";
    rows.forEach(function(p){
      var tr = document.createElement("tr");
      var state = p.state === "active" ? '<span class="tag ok">在游戏中</span>'
                : '<span class="tag wait">' + esc(p.state) + '</span>';
      tr.innerHTML = '<td>' + p.id + '</td><td>' + esc(p.name) + '</td><td>' + state + '</td><td>' +
        (p.bot ? '<span class="tag bot">人机</span>' : '<span class="tag">真人</span>') + '</td>';
      tb.appendChild(tr);
    });
    $("playersEmpty").hidden = rows.length > 0;

    renderMaps(s.maps || []);

    if (s.log && s.log.length){
      s.log.forEach(function(line){
        var cls = line.indexOf("[") === 0 ? "sys" : (line.indexOf("> ") === 0 ? "echo" : "");
        if (/未完成|失败|错误/.test(line)) cls = "err";
        logLine(line, cls);
      });
    }
    if (s.seq) seq = s.seq;

    var wasBusy = document.body.dataset.busy === "1";
    document.body.dataset.busy = s.busy ? "1" : "0";
    if (s.busy !== wasBusy) busy(!!s.busy);
  }

  function tick(){
    api("/api/state?since=" + seq).then(function(s){
      if (s.auth === false) { gate(true); return; }
      if (!s.ok) { toast(s.error || "读取失败", true); return; }
      gate(false);
      render(s);
      if (!timer) timer = setInterval(tick, 3000);
    }).catch(function(){
      $("dot").className = "dot";
      $("conn").textContent = "连接中断，正在重试…";
      if (!timer) timer = setInterval(tick, 3000);
    });
  }

  function send(){
    var text = $("cmd").value.trim();
    if (!text) return;
    $("cmd").value = "";
    logLine("> " + text, "echo");
    api("/api/command", { command: text }).then(function(r){
      if (!r.ok) toast(r.error, true); else setTimeout(tick, 400);
    }).catch(function(e){ toast("请求失败：" + e.message, true); });
  }

  $("login").onclick = function(){
    var token = $("token").value.trim();
    if (!token) return;
    api("/api/login", { token: token }).then(function(r){
      if (!r.ok) { toast(r.error || "口令不对", true); return; }
      document.cookie = "lantern=" + token + "; path=/; max-age=86400; samesite=strict";
      $("token").value = ""; seq = 0; gate(false); tick();
    }).catch(function(e){ toast("请求失败：" + e.message, true); });
  };
  $("token").onkeydown = function(e){ if (e.key === "Enter") $("login").click(); };

  $("send").onclick = send;
  $("cmd").onkeydown = function(e){ if (e.key === "Enter") send(); };
  $("clearLog").onclick = function(){ $("log").textContent = ""; };
  $("mapFilter").oninput = function(e){ filter = e.target.value.trim().toLowerCase(); if (lastState) renderMaps(lastState.maps || []); };

  document.querySelectorAll("[data-bot]").forEach(function(b){
    b.onclick = function(){
      var action = b.dataset.bot, body = { action: action };
      if (action === "add") { body.team = b.dataset.team; body.count = parseInt($("botCount").value, 10) || 1; }
      if (action === "balance") { body.count = parseInt($("balanceTotal").value, 10) || 10; }
      if (action === "remove" && !confirm("清除服务器上的所有人机？")) return;
      busy(true);
      api("/api/bots", body).then(function(r){
        busy(false);
        if (r.ok) { toast(r.detail || "已执行"); setTimeout(tick, 600); }
        else toast(r.error, true);
      }).catch(function(e){ busy(false); toast("请求失败：" + e.message, true); });
    };
  });

  // The launcher can hand the token over in the fragment, which the browser
  // never sends to the server. Cleared from the address bar immediately so it
  // does not linger in history or a screenshot.
  (function(){
    var m = /(?:^#|&)token=([^&]+)/.exec(location.hash || "");
    if (!m) return;
    var token = decodeURIComponent(m[1]);
    history.replaceState(null, "", location.pathname);
    api("/api/login", { token: token }).then(function(r){
      if (!r.ok) { gate(true); return; }
      document.cookie = "lantern=" + token + "; path=/; max-age=86400; samesite=strict";
      seq = 0; gate(false); tick();
    }).catch(function(){ gate(true); });
  })();

  tick();
})();
</script>
</body>
</html>
""";
}
