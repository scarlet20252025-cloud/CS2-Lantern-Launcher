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
  .wrap{max-width:1180px;margin:0 auto;padding:14px 16px 48px}
  header{display:flex;align-items:baseline;gap:12px;flex-wrap:wrap;margin-bottom:11px}
  .logo{font-weight:700;letter-spacing:.28em;font-size:17px}
  .sub{color:var(--muted);font-size:11.5px}
  .pill{margin-left:auto;display:flex;gap:8px;align-items:center;font-size:12px;color:var(--muted)}
  .dot{width:8px;height:8px;border-radius:50%;background:var(--dim);display:inline-block}
  .dot.on{background:var(--green);box-shadow:0 0 8px #92BA9A88}
  .dot.busy{background:var(--amber)}
  .card{background:var(--card);border:1px solid var(--line);border-radius:11px;padding:13px;margin-bottom:10px}
  /* Main column plus a fixed players rail; the rail is the thing you glance at
     while playing, so it must not be something you scroll to find. */
  .cols{display:grid;grid-template-columns:minmax(0,1fr) 318px;gap:10px;align-items:start}
  .side{position:sticky;top:10px}
  @media (max-width:900px){ .cols{grid-template-columns:1fr} .side{position:static;order:-1} }
  h2{font-size:14px;margin:0 0 9px;font-weight:600;display:flex;align-items:center;gap:8px}
  h2 .hint{font-weight:400;font-size:11.5px;color:var(--dim);margin-left:auto}
  .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(104px,1fr));gap:8px}
  .stat{background:var(--card2);border-radius:8px;padding:8px 11px}
  .stat b{display:block;font-size:17px;line-height:1.35;font-weight:600;font-variant-numeric:tabular-nums}
  .stat span{color:var(--muted);font-size:11px}
  .mapname{font-family:Consolas,ui-monospace,monospace;color:var(--accent);word-break:break-all}
  table{width:100%;border-collapse:collapse;font-size:13px}
  th{text-align:left;color:var(--dim);font-weight:500;font-size:11px;padding:3px 8px;border-bottom:1px solid var(--line)}
  td{padding:5px 8px;border-bottom:1px solid #202d40;font-variant-numeric:tabular-nums}
  tr:last-child td{border-bottom:none}
  .tag{font-size:10.5px;padding:1px 6px;border-radius:5px;border:1px solid var(--line);color:var(--muted);white-space:nowrap}
  .tag.bot{color:#8FA6BF;border-color:#2f4a63}
  .tag.human{color:#0F1A0F;background:var(--green);border-color:var(--green);font-weight:600}
  .tag.ok{color:var(--green);border-color:#33523d}
  .tag.wait{color:var(--amber);border-color:#5a4527}
  .empty{color:var(--dim);font-size:12.5px;padding:8px 0}
  button{font:inherit;cursor:pointer;border-radius:8px;border:1px solid var(--line);
         background:#253246;color:var(--text);padding:7px 12px;transition:.12s}
  button:hover:not(:disabled){border-color:var(--amber);background:#33445C}
  button:active:not(:disabled){background:#1B2637;border-color:#FFBF79}
  button:disabled{opacity:.45;cursor:not-allowed}
  button.primary{background:var(--amber);color:#141C25;font-weight:600;border-color:transparent}
  button.primary:hover:not(:disabled){background:#FFCB8F;border-color:transparent}
  button.primary:active:not(:disabled){background:#E9A75C;border-color:transparent}
  button.warn:hover:not(:disabled){background:#4A362A;border-color:var(--amber)}
  button.warn{background:#3a2b22;color:#FFD09A;border-color:#5a4527}
  button.ghost{background:transparent}
  .maps{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:7px;max-height:min(52vh,460px);overflow:auto;padding-right:4px}
  .map{display:flex;flex-direction:row;align-items:center;gap:10px;background:var(--card2);
     border:1px solid var(--line);border-radius:9px;padding:8px 10px;text-align:left;overflow:hidden}
  .map.cur{border-color:var(--amber);background:#302B26}
  .map .cover{width:62px;height:62px;flex:0 0 auto;border-radius:6px;background:#253346;
            display:flex;align-items:center;justify-content:center;color:#7A8CA4;
            font:8px Consolas,monospace;letter-spacing:.04em;text-align:center;overflow:hidden}
  .map .cover img{width:100%;height:100%;object-fit:cover;display:block}
  .map .body{display:flex;flex-direction:column;gap:3px;padding:0;flex:1;min-width:0}
  .map .t{font-weight:600;font-size:12.5px;line-height:1.3;min-height:2.6em;
        display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
  .map .m{font-family:Consolas,monospace;font-size:11px;color:var(--accent);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .note{color:var(--muted);font-size:11.5px;margin:0 0 8px;line-height:1.6}
  .prog{display:none;margin:0 0 10px;padding:9px 11px;border-radius:8px;
        background:#132A33;border:1px solid #27505C}
  .prog[data-on="1"]{display:block}
  .prog .bar{height:3px;border-radius:2px;background:#1E3B45;overflow:hidden;margin-bottom:7px}
  .prog .bar i{display:block;height:100%;width:38%;border-radius:2px;background:var(--amber);
               animation:slide 1.15s ease-in-out infinite}
  @keyframes slide{0%{transform:translateX(-110%)}100%{transform:translateX(370%)}}
  .prog .txt{color:#BFD9E2;font-size:12px;line-height:1.55}
  .map .act{display:flex;gap:6px;flex:0 0 auto;align-items:center}
  .map .b{margin:0;padding:6px 11px;font-size:12px;flex:0 0 auto;text-decoration:none;
           transition:background .12s,border-color .12s,filter .12s}
  .map .shop{padding:6px 9px;font-size:11.5px;color:#A9C6E8;border-color:#2f4a63;border-radius:8px;
             border:1px solid #2f4a63}
  .map .shop:hover{background:#2C4A66;border-color:#9CC9EC;color:#E6F3FF}
  .map .b.primary:hover{filter:brightness(1.12)}
  .map .b.ghost:hover{background:#3C4F6B;border-color:var(--amber)}
  .hero{display:flex;gap:12px;align-items:stretch;margin-bottom:9px}
  .hero[hidden]{display:none}
  #mapsEmpty[hidden],#playersEmpty[hidden],#app[hidden]{display:none}
  .hero .shot{width:126px;flex:0 0 auto;height:71px;border-radius:8px;overflow:hidden;background:#253246;
              display:flex;align-items:center;justify-content:center;color:#7A8CA4;font:11px Consolas,monospace}
  .hero .shot img{width:100%;height:100%;object-fit:cover;display:block}
  .hero .meta{display:flex;flex-direction:column;justify-content:center;gap:4px;min-width:0}
  .hero .meta b{font-size:15.5px;font-weight:600}
  .hero .meta span{color:var(--muted);font-size:12px;overflow-wrap:anywhere}
  @media (max-width:560px){ .hero{flex-direction:column} .hero .shot{width:100%;height:150px} }
  #log{background:#0A121D;border:1px solid var(--line);border-radius:9px;height:240px;overflow:auto;
       padding:9px 11px;font:11.5px/1.55 Consolas,ui-monospace,monospace;white-space:pre-wrap;word-break:break-word;color:#C6D3E4}
  #log .sys{color:var(--amber)}
  #log .err{color:var(--red)}
  #log .echo{color:#8FA6BF}
  .row{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
  .row input{flex:1 1 240px;min-width:0}
  /* Bot actions: label / number / buttons, one line each. A stretched grid put
     the second team button at the far edge and the number box ate the row. */
  .acts{display:flex;flex-direction:column;gap:8px}
  .acts .arow{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
  .acts .lbl{color:var(--dim);font-size:11.5px;flex:0 0 62px}
  .acts .num{width:64px;flex:0 0 64px}
  .acts button{padding:7px 12px;font-size:12.5px}
  .acts .push{margin-left:auto}
  @media (max-width:620px){ .acts .push{margin-left:0;width:100%} }
  .chips{display:flex;gap:6px;flex-wrap:wrap}
  .chips button{padding:6px 11px;font-size:12px;border-radius:7px}
  input{font:inherit;background:#0F1A28;border:1px solid var(--line);border-radius:8px;
        color:var(--text);padding:8px 11px;width:100%}
  input:focus{outline:none;border-color:var(--amber)}
  .num{width:74px;flex:0 0 auto;text-align:center}
  .toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);background:#22303f;
         border:1px solid var(--line);border-radius:10px;padding:11px 18px;font-size:13px;
         max-width:min(90vw,560px);opacity:0;pointer-events:none;transition:.2s;z-index:9}
  .toast.show{opacity:1}
  .toast.bad{border-color:#6b3a3a;background:#2b1d1f;color:#FFD0D0}
  .foot{color:var(--dim);font-size:11px;margin-top:12px;line-height:1.65}
  .join{background:#12251c;border:1px solid #2d5340;border-radius:10px;padding:10px 12px}
  .join .cmd{font:12.5px/1.5 Consolas,ui-monospace,monospace;color:#C8ECD6;background:#0A121D;
             border:1px solid #26402f;border-radius:8px;padding:9px 11px;overflow-wrap:anywhere;
             user-select:all;-webkit-user-select:all}
  .join .steps{color:#9FC0AC;font-size:11.5px;margin-top:7px;line-height:1.6}
  .join .steps b{color:var(--accent)}
  .join .cmdrow{display:flex;gap:8px;align-items:stretch;margin-bottom:8px}
  .join .addrrow{display:flex;gap:8px;align-items:center;margin-top:8px}
  .join .addrrow .lbl{color:var(--muted);font-size:11.5px;flex:0 0 auto}
  .join .addrrow input{flex:1;min-width:0;margin:0;padding:7px 10px}
  .join .addrrow button{flex:0 0 auto;padding:7px 14px}
  .join .joinbtn{display:flex;align-items:center;justify-content:center;flex:1;
                 padding:9px 16px;font-size:13.5px;font-weight:600;text-decoration:none;
                 background:var(--amber);color:#141C25;border:1px solid transparent;border-radius:8px}
  .join .joinbtn:hover{filter:brightness(1.08)}
  .join .joinbtn[hidden]{display:none}
  .join .cmdrow .cmd{flex:1;min-width:0;display:flex;align-items:center}
  .join .cmdrow button{flex:0 0 auto;padding:8px 16px}
  .join button.primary{background:#3E6B4F;color:#EAF6EE}
  .join .warnline{color:var(--amber);font-size:11.5px;margin-top:6px}
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

  <main id="app">
    <div class="card">
      <h2>服务器状态 <span class="hint" id="kindHint"></span></h2>
      <div class="prog" id="prog"><div class="bar"><i></i></div><div class="txt" id="progTxt">正在启动…</div></div>
      <div class="row" id="serverRow" style="margin:0 0 10px">
        <button class="primary" id="startServer">启动服务器</button>
        <button class="warn" id="restartServer">重启服务器</button>
        <span class="note" id="serverHint" style="margin:0"></span>
      </div>
      <div class="hero" id="hero" hidden>
        <div class="shot" id="heroShot"><img id="heroImg" alt=""></div>
        <div class="meta"><b id="heroTitle">—</b><span id="heroSub"></span></div>
      </div>
      <div class="stats">
        <div class="stat"><b id="sMap" class="mapname">—</b><span>当前地图</span></div>
        <div class="stat"><b id="sHumans">0</b><span>在线真人</span></div>
        <div class="stat"><b id="sBots">0</b><span>人机</span></div>
        <div class="stat"><b id="sPort">—</b><span>UDP 端口</span></div>
        <div class="stat"><b id="sCap">—</b><span>人数上限（启动参数）</span></div>
      </div>
      <div class="foot" id="addrLine"></div>
    </div>

    <div class="cols">
    <div>
    <div class="card">
      <h2>加入这台服务器 <span class="hint" id="joinHint"></span></h2>
      <div class="join">
        <div class="cmdrow">
          <a class="b primary joinbtn" id="joinLink" href="#" hidden>加入服务器</a>
          <button class="primary" id="copyJoin">复制指令</button>
        </div>
        <div class="cmd" id="joinCmd">服务器开图后这里会出现加入指令。</div>
        <div class="addrrow">
          <span class="lbl">对外地址</span>
          <input id="joinAddr" placeholder="39.108.93.52:10003" spellcheck="false" autocomplete="off">
          <button id="saveAddr" class="ghost">保存</button>
        </div>
        <div class="steps">点「加入服务器」Steam 会直接带参数拉起 CS2 连进来；浏览器会先问一次是否允许打开 Steam。复制指令后也可以 <b>Win + R</b> 粘贴回车。</div>
        <div class="warnline" id="joinWarn" hidden></div>
      </div>
    </div>


    <div class="card">
      <h2>换地图 <span class="hint">换图会保留房间，但正在加载的玩家可能掉线</span></h2>
      <p class="note">好友没有这张图？点卡片上的「订阅」打开 Steam 工坊页，订阅后由 Steam 下载；已订阅过的只会显示“已订阅”，不会重复下载。</p>
      <div class="row" style="margin-bottom:10px">
        <input id="mapFilter" placeholder="筛选地图名称或文件名…">
      </div>
      <div class="maps" id="maps"></div>
      <div class="empty" id="mapsEmpty">这台机器上没有可开的地图。</div>
    </div>

    <div class="card">
      <h2>人机与对局 <span class="hint">人数含真人；命令直接下发到服务器</span></h2>
      <div class="acts">
        <div class="arow">
          <span class="lbl">人机数量</span>
          <input id="botCount" class="num" type="number" min="1" max="32" value="1" title="每次添加的人机数量">
          <button data-bot="add" data-team="t">加 T 人机</button>
          <button data-bot="add" data-team="ct">加 CT 人机</button>
          <button data-bot="remove" class="warn push" title="bot_quota 0; bot_kick">清除所有人机</button>
        </div>
        <div class="arow">
          <span class="lbl">按总人数</span>
          <input id="balanceTotal" class="num" type="number" min="2" max="64" value="10" title="补位后总人数（含真人）">
          <button data-bot="balance">补齐到该人数</button>
        </div>
      </div>

      <h2 style="margin-top:14px">对局操作</h2>
      <div class="chips">
        <button data-quick="restart">重开对局</button>
        <button data-quick="swap">交换阵营</button>
        <button data-quick="pause">暂停</button>
        <button data-quick="unpause">继续</button>
        <button data-quick="warmup_on">开始热身</button>
        <button data-quick="warmup_off">结束热身</button>
      </div>

      <h2 style="margin-top:14px">人机难度</h2>
      <div class="chips">
        <button data-quick="diff0">简单</button>
        <button data-quick="diff1">普通</button>
        <button data-quick="diff2">困难</button>
        <button data-quick="diff3">专家</button>
      </div>

      <h2 style="margin-top:14px">练习与权限</h2>
      <div class="chips">
        <button data-quick="cheats_on">启用练习权限</button>
        <button data-quick="cheats_off">关闭练习权限</button>
        <button data-quick="ammo_on">无限弹药</button>
        <button data-quick="ammo_off">正常弹药</button>
        <button data-quick="practice_money">练习经济 60000</button>
      </div>
    </div>

    </div><!-- /main column -->
    <aside class="side">
    <div class="card">
      <h2>谁在房间里 <span class="hint">只有进入地图的人算“就绪”</span></h2>
      <table>
        <thead><tr><th style="width:38px">槽位</th><th>名字</th><th style="width:74px">状态</th><th style="width:50px">类型</th></tr></thead>
        <tbody id="players"></tbody>
      </table>
      <div class="empty" id="playersEmpty">还没有人。</div>
      <button class="ghost" id="moreBots" style="width:100%;margin-top:8px;display:none"></button>
    </div>
    </aside>
    </div><!-- /cols -->

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
      这里可以启动和重启服务器，但没有「关闭」：停服会结束所有人的对局，只有坐在那台电脑前的人能决定。<br>
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

  var joinCommand = "";
  var botReveal = 0;
  var stageSeen = "";

  function renderJoin(sv, join){
    var box = $("joinCmd"), warn = $("joinWarn"), hint = $("joinHint");
    warn.hidden = true;
    if (!sv.running){
      joinCommand = "";
      box.textContent = "服务器还没开图，开好后这里会出现加入指令。";
      hint.textContent = "";
      $("copyJoin").disabled = true;
      return;
    }
    // Prefer what the host configured (a public IP or domain they forwarded).
    var command = join.command || "";
    var source = join.source || "";
    if (!command){
      // Fall back to the host this page was reached on: a forwarded web port
      // usually lives on the same machine as the forwarded game port. Say so
      // instead of pretending it is authoritative.
      var host = location.hostname;
      if (host && host !== "127.0.0.1" && host !== "localhost" && sv.port){
        command = "steam://run/730//+connect " + host + ":" + sv.port + "/";
        source = "按你打开本页的地址推断";
      }
    }
    joinCommand = command;
    // A plain <a href="steam://…"> is the same mechanism the 订阅 button uses:
    // the browser asks once, then Steam takes over. No console, no Win+R.
    var addr = $("joinAddr");
    if (addr && document.activeElement !== addr) addr.value = join.configured || "";
    var link = $("joinLink");
    if (join.link){
      link.href = join.link; link.hidden = false;
      link.textContent = "加入服务器";
    } else { link.hidden = true; link.removeAttribute("href"); }
    if (!command){
      box.textContent = "房主还没填对外地址。请在启动器「好友联机」页填上公网 IP 或域名，或让房主把这条指令发给你。";
      hint.textContent = "";
      $("copyJoin").disabled = true;
      if (join.error){ warn.hidden = false; warn.textContent = join.error; }
      return;
    }
    box.textContent = command;
    hint.textContent = source ? "来源：" + source : "";
    $("copyJoin").disabled = false;
    if (join.error){ warn.hidden = false; warn.textContent = join.error; }
  }

  function copyJoin(){
    if (!joinCommand) return;
    var done = function(){ toast("已复制。按 Win + R 打开“运行”，粘贴，回车。"); };
    if (navigator.clipboard && navigator.clipboard.writeText){
      navigator.clipboard.writeText(joinCommand).then(done, function(){ fallbackCopy(); });
    } else fallbackCopy();
    function fallbackCopy(){
      // http:// on a LAN address is not a "secure context", so the async
      // clipboard API is unavailable exactly where this page is most used.
      var area = document.createElement("textarea");
      area.value = joinCommand; area.style.position = "fixed"; area.style.opacity = "0";
      document.body.appendChild(area); area.select();
      try { document.execCommand("copy"); done(); }
      catch (e) { toast("复制失败，请手动选中上面那一行。", true); }
      document.body.removeChild(area);
    }
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
      var cover = document.createElement("div");
      cover.className = "cover";
      if (m.cover){
        var img = document.createElement("img");
        img.loading = "lazy"; img.alt = "";
        // The token lives in a cookie, so a plain <img> is already authorised.
        img.src = "/api/cover?identity=" + encodeURIComponent(m.identity);
        img.onerror = function(){ cover.textContent = "WORKSHOP"; };
        cover.appendChild(img);
      } else { cover.textContent = m.official ? "CS2" : "WORKSHOP"; }
      card.appendChild(cover);
      var body = document.createElement("div");
      body.className = "body";
      body.innerHTML = '<div class="t">' + esc(m.title) + '</div><div class="m">' + esc(m.mapName) + '</div>';
      card.appendChild(body);
      var act = document.createElement("div");
      act.className = "act";
      if (m.workshopUrl){
        // Steam has no protocol that subscribes silently, so this opens the item
        // page: subscribing there is the download, and an item already owned
        // shows 已订阅 instead of downloading twice. href= so the browser asks.
        var shop = document.createElement("a");
        shop.className = "b shop";
        shop.href = m.workshopUrl;
        shop.textContent = "订阅";
        shop.title = "打开这张图的 Steam 创意工坊页；已订阅过的会显示“已订阅”，不会重复下载";
        act.appendChild(shop);
      }
      var b = document.createElement("button");
      b.className = "b" + (m.current ? " ghost" : " primary");
      b.textContent = m.current ? "当前地图" : (lastState && lastState.server && lastState.server.running ? "换这张" : "启动并开这张");
      b.disabled = m.current || (lastState && lastState.busy);
      b.onclick = function(){ switchMap(m); };
      act.appendChild(b);
      card.appendChild(act);
      host.appendChild(card);
    });
    $("mapsEmpty").hidden = shown > 0;
  }

  function switchMap(m){
    // No confirm(): a blocking browser dialog is exactly what the host asked to
    // avoid, and the button already names the map it will load.
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

    // A launch can take a minute and a half; without a stage the page looked
    // frozen. Show the launcher's own narration, not a fake percentage.
    var prog = $("prog");
    prog.dataset.on = s.busy ? "1" : "0";
    if (s.busy){
      var stage = (s.stage || "").trim();
      stageSeen = stage || stageSeen;
      $("progTxt").textContent = stage || "服务器正在启动，加载地图通常需要 30–90 秒…";
    } else if (stageSeen){
      // Keep the last stage visible for one poll after finishing, so the page
      // does not blink back to "服务器未加载地图" before the state catches up.
      stageSeen = "";
    }
    $("sMap").textContent = sv.map || "—";
    $("sHumans").textContent = sv.humans || 0;
    $("sBots").textContent = sv.bots || 0;
    $("sPort").textContent = sv.port || "—";
    $("sCap").textContent = sv.capacity == null ? "未知" : sv.capacity;
    $("kindHint").textContent = sv.running ? ("工坊 " + (sv.addon || "官方地图")) : "";

    // Start only makes sense when nothing runs, restart only when something does.
    // Both stay visible so the page's capability is obvious before you need it.
    var canManage = !sv.listen;
    $("startServer").style.display = canManage ? "" : "none";
    $("restartServer").style.display = canManage ? "" : "none";
    $("startServer").disabled = !canManage || sv.running || !!s.busy;
    $("restartServer").disabled = !canManage || !sv.running || !!s.busy;
    $("serverHint").textContent = sv.listen
      ? "房主用的是「本机边玩边开」，网页无法控制这种模式。"
      : (sv.running ? "点地图卡片可以换图；换图会保留房间。" : "服务器没在运行：点「启动服务器」直接开，或点任意一张地图启动并加载它。");
    $("addrLine").textContent = sv.running
      ? ("服务器身份 " + (sv.identity || "未提供") + " · UDP " + sv.port)
      : "独立服务器尚未加载地图；启动器里开图后这里会自动出现。";

    // Humans first, then bots, and only the first ten bots until asked for more:
    // a full bot roster buried the two people you actually came to look at.
    // The engine reports free slots as id 65535 in state "challenging"; listing
    // them as 真人 buried the one person actually connected.
    var rows = (s.players || []).filter(function(p){
      return p.id !== 65535 && (p.name || "").trim() !== "";
    }).slice().sort(function(a, b){
      if (a.bot !== b.bot) return a.bot ? 1 : -1;
      return a.id - b.id;
    });
    var humans = rows.filter(function(p){ return !p.bot; });
    var bots = rows.filter(function(p){ return p.bot; });
    var shown = humans.concat(bots.slice(0, 10 + botReveal));
    var tb = $("players"); tb.textContent = "";
    shown.forEach(function(p){
      var tr = document.createElement("tr");
      var state = p.state === "active" ? '<span class="tag ok">在游戏中</span>'
                : '<span class="tag wait">' + esc(p.state) + '</span>';
      tr.innerHTML = '<td>' + (p.bot ? p.id : '<b>' + p.id + '</b>') + '</td>'
        + '<td>' + (p.bot ? '<span style="color:#9FB0C6">' + esc(p.name) + '</span>'
                          : '<b>' + esc(p.name) + '</b>') + '</td>'
        + '<td>' + state + '</td><td>' +
        (p.bot ? '<span class="tag bot">人机</span>' : '<span class="tag human">真人</span>') + '</td>';
      tb.appendChild(tr);
    });
    var hiddenBots = bots.length - Math.min(bots.length, 10 + botReveal);
    var more = $("moreBots");
    more.style.display = (hiddenBots > 0 || botReveal > 0) ? "" : "none";
    more.textContent = hiddenBots > 0
      ? ("还有 " + hiddenBots + " 个人机 · 展开")
      : "收起人机";
    more.onclick = function(){
      botReveal = hiddenBots > 0 ? botReveal + 50 : 0;
      if (lastState) render(lastState);
    };
    $("playersEmpty").hidden = rows.length > 0;

    renderJoin(sv, s.join || {});

    renderMaps(s.maps || []);

    // The hero shows the map the server itself reports, so a failed switch can
    // never be mistaken for a successful one because the picture changed.
    var all = s.maps || [];
    var currentEntry = null;
    for (var i = 0; i < all.length; i++) if (all[i].current) { currentEntry = all[i]; break; }
    if (sv.running && currentEntry){
      $("hero").hidden = false;
      $("heroTitle").textContent = currentEntry.title;
      $("heroSub").textContent = currentEntry.mapName + " · " + (sv.addon ? ("工坊 " + sv.addon) : "官方地图") +
        " · 真人 " + (sv.humans || 0) + " / 人机 " + (sv.bots || 0);
      var shot = $("heroShot"), picture = $("heroImg");
      if (currentEntry.cover){
        picture.style.display = "block";
        if (picture.getAttribute("src") !== "/api/cover?identity=" + encodeURIComponent(currentEntry.identity))
          picture.src = "/api/cover?identity=" + encodeURIComponent(currentEntry.identity);
      } else { picture.removeAttribute("src"); picture.style.display = "none"; shot.textContent = "CS2"; }
    } else {
      $("hero").hidden = true;
    }

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

  var timer = null;

  // A launch moves through stages in seconds; polling it every 3 s made the
  // progress line sit still long enough to look hung. Poll faster while work is
  // in flight, and fall back to the calm interval when it settles.
  function nextInterval(){
    if (document.body.dataset.busy === "1") return 1200;
    return 3000;
  }

  function tick(){
    api("/api/state?since=" + seq).then(function(s){
      if (!s.ok) { toast(s.error || "读取失败", true); return; }
      render(s);
      setTimeout(tick, nextInterval());
    }).catch(function(){
      $("dot").className = "dot";
      $("conn").textContent = "连接中断，正在重试…";
      setTimeout(tick, 3000);
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

  document.querySelectorAll("[data-quick]").forEach(function(el){
    el.onclick = function(){ quick(el.getAttribute("data-quick")); };
  });

  function quick(action){
    busy(true);
    return api("/api/quick", { action: action }).then(function(r){
      busy(false);
      if (r.ok) { toast(r.detail || "已执行"); setTimeout(tick, 600); }
      else toast(r.error, true);
    }).catch(function(e){ busy(false); toast("请求失败：" + e.message, true); });
  }

  function serverAction(action){
    busy(true);
    return api("/api/server", { action: action }).then(function(r){
      busy(false);
      if (r.ok) { toast(r.detail || "已执行"); setTimeout(tick, 800); }
      else toast(r.error, true);
    }).catch(function(e){ busy(false); toast("请求失败：" + e.message, true); });
  }
  $("startServer").onclick = function(){ serverAction("start"); };
  $("restartServer").onclick = function(){ serverAction("restart"); };

  $("copyJoin").onclick = copyJoin;
  $("send").onclick = send;
  $("cmd").onkeydown = function(e){ if (e.key === "Enter") send(); };
  $("clearLog").onclick = function(){ $("log").textContent = ""; };
  $("mapFilter").oninput = function(e){ filter = e.target.value.trim().toLowerCase(); if (lastState) renderMaps(lastState.maps || []); };

  document.querySelectorAll("[data-bot]").forEach(function(b){
    b.onclick = function(){
      var action = b.dataset.bot, body = { action: action };
      if (action === "add") { body.team = b.dataset.team; body.count = parseInt($("botCount").value, 10) || 1; }
      if (action === "balance") { body.count = parseInt($("balanceTotal").value, 10) || 10; }
      busy(true);
      api("/api/bots", body).then(function(r){
        busy(false);
        if (r.ok) { toast(r.detail || "已执行"); setTimeout(tick, 600); }
        else toast(r.error, true);
      }).catch(function(e){ busy(false); toast("请求失败：" + e.message, true); });
    };
  });

  // One line of config, editable where its effect is visible.
  (function(){
    var box = $("joinAddr"), save = $("saveAddr");
    save.onclick = function(){
      var value = (box.value || "").trim();
      save.disabled = true;
      api("/api/address", { address: value }).then(function(r){
        save.disabled = false;
        if (r.ok) { toast(r.detail || "已保存"); tick(); }
        else toast(r.error || "保存失败", true);
      }).catch(function(e){ save.disabled = false; toast("请求失败：" + e.message, true); });
    };
    box.addEventListener("keydown", function(e){ if (e.key === "Enter") save.click(); });
  })();

  tick();
})();
</script>
</body>
</html>
""";
}
