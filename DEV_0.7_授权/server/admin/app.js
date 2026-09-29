"use strict";
// 授权管理看板前端。所有数据用 textContent 渲染，不拼 HTML，客户名 / 备注里有什么字符都不会被当成代码。
(function () {
  var $ = function (id) { return document.getElementById(id); };
  var state = { csrf: null, me: null, codes: [], stats: null, filter: "all", plan: "Y", current: null, lastIssue: null };

  var STATE_TEXT = { active: "使用中", unused: "未激活", expired: "已过期", revoked: "已作废", unknown: "未知" };
  var FILTERS = [
    ["all", "全部"], ["active", "使用中"], ["unused", "未激活"], ["expiring", "即将到期"], ["expired", "已过期"], ["revoked", "已作废"]
  ];

  // ---------- 小工具 ----------

  function h(tag, props) {
    var el = document.createElement(tag);
    if (props) {
      for (var k in props) {
        if (!Object.prototype.hasOwnProperty.call(props, k) || props[k] == null) continue;
        if (k === "class") el.className = props[k];
        else if (k === "text") el.textContent = props[k];
        else if (k.slice(0, 2) === "on") el.addEventListener(k.slice(2), props[k]);
        else el.setAttribute(k, props[k]);
      }
    }
    for (var i = 2; i < arguments.length; i++) append(el, arguments[i]);
    return el;
  }
  function append(el, child) {
    if (child == null || child === false) return;
    if (Array.isArray(child)) { child.forEach(function (c) { append(el, c); }); return; }
    el.appendChild(typeof child === "string" || typeof child === "number" ? document.createTextNode(String(child)) : child);
  }
  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); }

  var toastTimer = null;
  function toast(text, isError) {
    var t = $("toast");
    t.hidden = true;
    void t.offsetWidth;  // 让入场动画每次都重播
    t.textContent = text;
    t.className = "toast" + (isError ? " error" : "");
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.hidden = true; }, isError ? 4500 : 2200);
  }

  function api(method, path, body) {
    var opt = { method: method, credentials: "same-origin", headers: {} };
    if (method !== "GET") {
      opt.headers["Content-Type"] = "application/json";
      opt.headers["X-TG-CSRF"] = state.csrf || "";
      opt.body = JSON.stringify(body || {});
    }
    return fetch(path, opt).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (data) {
        if (res.status === 401 && path !== "/admin/api/login") { showLogin(); throw new Error(data.error || "请先登录"); }
        if (!res.ok) throw new Error(data.error || ("请求失败（" + res.status + "）"));
        return data;
      });
    });
  }

  function copy(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).then(function () { toast("已复制"); }, function () { fallbackCopy(text); });
    }
    fallbackCopy(text);
    return Promise.resolve();
  }
  function fallbackCopy(text) {
    var ta = h("textarea", { readonly: "", "aria-hidden": "true" });
    ta.value = text;
    ta.className = "offscreen";
    ta.style.position = "fixed"; ta.style.left = "-9999px";
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand("copy"); toast("已复制"); } catch (e) { toast("复制失败，请手工选中复制", true); }
    document.body.removeChild(ta);
  }

  function fmtTime(s) { return s ? s.slice(5, 16) : "—"; }   // "2026-09-29 18:01:02" → "09-29 18:01"

  // ---------- 登录 ----------

  function showLogin() {
    $("app").hidden = true;
    closeDrawer();
    $("login").hidden = false;
    $("password").value = "";
    setTimeout(function () { $("password").focus(); }, 0);
  }

  $("login-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var btn = $("login-btn");
    btn.disabled = true;
    $("login-error").textContent = "";
    api("POST", "/admin/api/login", { password: $("password").value })
      .then(function (data) { state.csrf = data.csrf; return boot(); })
      .catch(function (err) {
        $("login-error").textContent = err.message;
        var card = $("login-form");
        card.classList.remove("shake"); void card.offsetWidth; card.classList.add("shake");
      })
      .then(function () { btn.disabled = false; });
  });

  $("logout").addEventListener("click", function () {
    api("POST", "/admin/api/logout").catch(function () {}).then(showLogin);
  });

  function boot() {
    return api("GET", "/admin/api/me").then(function (me) {
      state.me = me;
      state.csrf = me.csrf;
      $("login").hidden = true;
      $("app").hidden = false;
      renderHeader();
      renderPlans();
      return load(true);
    });
  }

  // ---------- 顶栏与签发表单 ----------

  function renderHeader() {
    var me = state.me, info = $("key-info");
    clear(info);
    if (me.canIssue) append(info, ["签发密钥 ", h("span", { class: "key-badge", text: me.keyId })]);
    else append(info, h("span", { class: "key-badge off", text: "只管理 · 不签发" }));
    $("today").textContent = "服务器日期 " + me.todayDate;
    $("issue-form").hidden = !me.canIssue;
    $("issue-disabled").hidden = me.canIssue;
  }

  function renderPlans() {
    var seg = $("plan-seg");
    clear(seg);
    state.me.plans.forEach(function (p, i) {
      if (p.letter === state.plan) seg.setAttribute("data-index", String(i));
      seg.appendChild(h("button", {
        type: "button", role: "radio", "aria-checked": String(p.letter === state.plan), text: p.name,
        onclick: function () { state.plan = p.letter; renderPlans(); }
      }));
    });
    var cur = state.me.plans.filter(function (p) { return p.letter === state.plan; })[0];
    $("plan-hint").textContent = cur ? cur.days + " 天，今天签发 → " + cur.expiry + " 到期（有效期从签发日起算）" : "";
  }

  $("issue-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var count = parseInt($("count").value, 10) || 1;
    var customer = $("customer").value.trim();
    if (!customer && !confirm("没有填客户名称，之后在列表里不好认。仍然签发？")) return;
    var btn = $("issue-btn");
    btn.disabled = true;
    btn.classList.add("busy");
    api("POST", "/admin/api/issue", { plan: state.plan, count: count, customer: customer, note: $("note").value.trim() })
      .then(function (res) {
        showResult(res);
        $("customer").value = ""; $("note").value = ""; $("count").value = 1;
        return load(true);
      })
      .catch(function (err) { toast(err.message, true); })
      .then(function () { btn.disabled = false; btn.classList.remove("busy"); });
  });

  // ---------- 签发结果 ----------

  function resultText(res) {
    var lines = ["天工工具箱 激活码", "档位：" + res.plan + "    到期：" + res.expiry + (res.customer ? "    客户：" + res.customer : ""), ""];
    res.codes.forEach(function (c, i) {
      if (res.codes.length > 1) lines.push("【" + (i + 1) + "】码ID " + c.display);
      else lines.push("码ID " + c.display);
      lines.push(c.code, "");
    });
    lines.push("使用方法：打开天工 CAD，点任意插件命令，把激活码整段粘贴进激活窗口，点「激活」（需要联网）。");
    return lines.join("\r\n");
  }

  function showResult(res) {
    state.lastIssue = res;
    $("result-title").textContent = "已签发 " + res.codes.length + " 个激活码";
    $("result-sub").textContent = res.plan + " · " + res.expiry + " 到期" + (res.customer ? " · " + res.customer : "");
    var list = $("result-list");
    clear(list);
    res.codes.forEach(function (c) {
      list.appendChild(h("div", { class: "result-item" },
        h("div", { class: "row" },
          h("b", { text: c.display }),
          h("button", { type: "button", class: "btn small", text: "复制", onclick: function () { copy(c.code); } })),
        h("div", { class: "code-box", text: c.code })));
    });
    $("result-dialog").showModal();
  }
  $("result-close").addEventListener("click", function () { $("result-dialog").close(); });
  $("result-copy").addEventListener("click", function () { if (state.lastIssue) copy(resultText(state.lastIssue)); });
  $("result-download").addEventListener("click", function () {
    var res = state.lastIssue;
    if (!res) return;
    var blob = new Blob(["﻿" + resultText(res)], { type: "text/plain;charset=utf-8" });
    var a = h("a", { href: URL.createObjectURL(blob), download: "激活码_" + res.plan + "_" + (res.customer || res.codes[0].display) + ".txt" });
    document.body.appendChild(a);
    a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); document.body.removeChild(a); }, 1000);
  });

  // ---------- 列表 ----------

  // animate=true：用户主动触发的刷新才播放入场动画；每分钟的自动刷新静默更新。
  function load(animate) {
    return api("GET", "/admin/api/codes").then(function (data) {
      state.codes = data.codes;
      state.stats = data.stats;
      renderStats();
      renderFilters();
      renderRows(animate);
    });
  }

  // 数字从旧值滚到新值
  var shown = {};
  function countUp(el, key, to) {
    var from = shown[key] == null ? 0 : shown[key];
    shown[key] = to;
    if (from === to || window.matchMedia("(prefers-reduced-motion: reduce)").matches) { el.textContent = to; return; }
    var start = null, dur = 700;
    function step(ts) {
      if (start === null) start = ts;
      var p = Math.min((ts - start) / dur, 1), eased = 1 - Math.pow(1 - p, 3);
      el.textContent = Math.round(from + (to - from) * eased);
      if (p < 1) requestAnimationFrame(step);
    }
    requestAnimationFrame(step);
  }

  function isExpiring(c) { return (c.state === "active" || c.state === "unused") && c.daysLeft != null && c.daysLeft <= 14; }

  function matches(c) {
    if (state.filter === "expiring" ? !isExpiring(c) : (state.filter !== "all" && c.state !== state.filter)) return false;
    var q = $("search").value.trim().toLowerCase().replace(/-/g, "");
    if (!q) return true;
    return [c.id, c.customer, c.note, c.machine, c.lastIp].some(function (v) {
      return v && String(v).toLowerCase().replace(/-/g, "").indexOf(q) >= 0;
    });
  }

  function renderStats() {
    var s = state.stats, box = $("stats");
    var first = !box.firstChild;
    clear(box);
    var tiles = [
      ["active", "ok", "使用中", s.active, "已绑定机器"],
      ["unused", "info", "未激活", s.unused, "已签发、还没人用"],
      ["expiring", "warn", "14 天内到期", s.expiring, "该提醒续费了"],
      ["revoked", "bad", "已作废", s.revoked, "约 1 天内全部停用"],
      [null, "accent", "今日签发", s.issuedToday, "上限 " + s.issueLimit + " 个"],
      [null, "off", "24 小时联网", s.checks24h, s.rejected24h ? "其中被拒 " + s.rejected24h + " 次" : "激活与复核"]
    ];
    tiles.forEach(function (t, i) {
      var value = h("div", { class: "v", text: shown["s" + i] == null ? 0 : shown["s" + i] });
      var tile = h("button", {
        type: "button", class: "stat tone-" + t[1], title: t[0] ? "只看" + t[2] : null,
        onclick: t[0] ? function () { state.filter = t[0]; renderFilters(); renderRows(true); } : null
      },
        h("div", { class: "k" }, h("span", { class: "dot" }), t[2]),
        value,
        h("div", { class: "s", text: t[4] }));
      if (!first) tile.style.animation = "none";  // 只有第一次渲染时卡片入场
      box.appendChild(tile);
      countUp(value, "s" + i, t[3]);
    });
  }

  function renderFilters() {
    var box = $("filters");
    clear(box);
    FILTERS.forEach(function (f) {
      var n = f[0] === "all" ? state.codes.length
        : state.codes.filter(function (c) { return f[0] === "expiring" ? isExpiring(c) : c.state === f[0]; }).length;
      box.appendChild(h("button", {
        type: "button", class: "chip", role: "tab", "aria-selected": String(state.filter === f[0]),
        onclick: function () { state.filter = f[0]; renderFilters(); renderRows(true); }
      }, f[1], h("b", { text: n })));
    });
  }

  function badge(st) { return h("span", { class: "badge " + st, text: STATE_TEXT[st] || st }); }

  function renderRows(animate) {
    var body = $("rows");
    clear(body);
    var list = state.codes.filter(matches);
    list.forEach(function (c, i) {
      var expiry = [c.expiry || "—"];
      if (isExpiring(c)) expiry.push(h("span", { class: "soon", text: c.daysLeft + " 天后到期" }));
      var tr = h("tr", { tabindex: "0", onclick: function () { openDrawer(c.id); },
        onkeydown: function (e) { if (e.key === "Enter") openDrawer(c.id); } },
        h("td", { class: "mono id-cell", "data-label": "码ID", text: c.display }),
        h("td", { class: "cust", "data-label": "客户" }, h("div", null, c.customer || h("span", { class: "muted", text: "—" }),
          c.note ? h("span", { class: "sub", text: c.note }) : null)),
        h("td", { "data-label": "档位", text: c.plan || "—" }),
        h("td", { "data-label": "到期" }, h("div", null, expiry)),
        h("td", { class: "state-cell", "data-label": "状态" }, badge(c.state)),
        h("td", { class: "mono", "data-label": "机器", text: c.machine || "—" }),
        h("td", { "data-label": "换机", text: c.rebindsUsed + " / " + c.quota }),
        h("td", { class: "muted", "data-label": "最近联网", text: fmtTime(c.lastSeen) }));
      if (animate) { tr.className = "enter"; tr.style.animationDelay = Math.min(i * 28, 420) + "ms"; }
      body.appendChild(tr);
    });
    $("empty").hidden = list.length > 0;
  }

  $("search").addEventListener("input", function () { renderRows(false); });
  $("refresh").addEventListener("click", function () { load(true).then(function () { toast("已刷新"); }, function (e) { toast(e.message, true); }); });

  // ---------- 详情抽屉 ----------

  var OP_TEXT = { activate: "激活", check: "复核" };
  function eventText(ev) {
    if (ev.status === "ok") return { cls: "ok", text: { bound: "首次绑定", already: "正常", rebound: "换机转入" }[ev.result] || "通过" };
    return { cls: "bad", text: { moved: "拒绝：已转到别的机器", quota: "拒绝：换机次数用完", revoked: "拒绝：已作废", expired: "拒绝：已过期" }[ev.status] || ev.status };
  }
  var AUDIT_TEXT = { issue: "签发", revoke: "作废", unrevoke: "恢复", unbind: "解绑", quota: "调整换机次数", meta: "修改客户/备注" };

  function openDrawer(id) {
    api("GET", "/admin/api/codes/" + id).then(renderDrawer, function (e) { toast(e.message, true); });
  }
  function closeDrawer() {
    var dr = $("drawer"), scrim = $("scrim");
    state.current = null;
    if (dr.hidden) return;
    dr.classList.add("closing");
    scrim.classList.add("closing");
    setTimeout(function () {
      dr.hidden = true; scrim.hidden = true;
      dr.classList.remove("closing"); scrim.classList.remove("closing");
    }, 200);
  }
  $("scrim").addEventListener("click", closeDrawer);
  document.addEventListener("keydown", function (e) { if (e.key === "Escape" && !$("drawer").hidden) closeDrawer(); });

  function act(action, body, confirmText) {
    if (confirmText && !confirm(confirmText)) return;
    api("POST", "/admin/api/codes/" + state.current.id + "/" + action, body || {})
      .then(function (d) { renderDrawer(d); toast("已保存"); return load(); })
      .catch(function (e) { toast(e.message, true); });
  }

  function renderDrawer(d) {
    state.current = d;
    var dr = $("drawer");
    clear(dr);

    var customer = h("input", { maxlength: "100", placeholder: "客户" });
    customer.value = d.customer || "";
    var note = h("input", { maxlength: "200", placeholder: "备注" });
    note.value = d.note || "";
    var quota = h("input", { type: "number", min: "0", max: "99", inputmode: "numeric" });
    quota.value = d.quota;

    var meta = [
      ["档位", d.plan || "—"],
      ["签发日", d.issueDate || "—"],
      ["到期", d.expiry ? d.expiry + (d.daysLeft > 0 ? "（剩 " + d.daysLeft + " 天）" : "") : "—"],
      ["绑定机器", d.machine || "未绑定"],
      ["绑定时间", d.boundAt || "—"],
      ["最近联网", d.lastSeen ? d.lastSeen + (d.lastIp ? "  ·  " + d.lastIp : "") : "从未"],
      ["换机次数", "已用 " + d.rebindsUsed + " / 共 " + d.quota + (d.quotaCustom ? "（单独设置）" : "（默认）")],
      ["来源", { web: "看板签发", "import": "台账导入" }[d.source] || "客户端联网时登记"]
    ];

    var timeline = d.events.map(function (ev) {
      var t = eventText(ev);
      return h("li", { class: t.cls === "bad" ? "is-bad" : null }, h("time", { text: ev.at }),
        h("div", null, (OP_TEXT[ev.op] || ev.op) + " · ", h("span", { class: t.cls, text: t.text }),
          h("div", { class: "sub mono", text: "机器 " + ev.machine + "  ·  " + ev.ip })));
    });
    var audit = d.audit.map(function (a) {
      return h("li", null, h("time", { text: a.at }),
        h("div", null, AUDIT_TEXT[a.action] || a.action, a.detail ? h("div", { class: "sub", text: a.detail }) : null,
          h("div", { class: "sub mono", text: a.ip })));
    });

    append(dr, [
      h("div", { class: "drawer-head" },
        h("div", null, h("span", { class: "id", text: d.display }), badge(d.state)),
        h("button", { type: "button", class: "icon-btn", "aria-label": "关闭", text: "×", onclick: closeDrawer })),
      h("div", { class: "drawer-body" },
        h("div", { class: "section" }, h("dl", { class: "meta" }, meta.map(function (m) {
          return [h("dt", { text: m[0] }), h("dd", { text: m[1] })];
        }))),
        h("div", { class: "section" }, h("h3", { text: "激活码" }),
          d.code ? h("div", { class: "stack" }, h("div", { class: "code-box", text: d.code }),
            h("button", { type: "button", class: "btn small", text: "复制激活码", onclick: function () { copy(d.code); } }))
            : h("p", { class: "hint", text: "服务器上没有这个码的全文：它是用管理员工具签发、客户联网时才登记的（可用 import-ledger 导入台账补全）。" })),
        h("div", { class: "section" }, h("h3", { text: "客户与备注" }),
          h("div", { class: "stack" }, customer, note,
            h("button", { type: "button", class: "btn small", text: "保存", onclick: function () {
              act("meta", { customer: customer.value, note: note.value });
            } }))),
        h("div", { class: "section" }, h("h3", { text: "操作" }),
          h("div", { class: "stack" },
            h("div", { class: "actions" },
              d.state === "revoked"
                ? h("button", { type: "button", class: "btn", text: "撤销作废", onclick: function () { act("unrevoke"); } })
                : h("button", { type: "button", class: "btn danger", text: "作废", onclick: function () {
                    act("revoke", {}, "作废 " + d.display + "？\n已激活的机器会在下次联网复核（约 1 天内）时停用。");
                  } }),
              h("button", { type: "button", class: "btn", text: "解绑机器", disabled: d.machine ? null : "",
                onclick: function () { act("unbind", {}, "解绑 " + d.display + "？\n下一台输入这个码的电脑直接绑定，不占换机次数；当前机器下次复核后停用。"); } })),
            h("div", { class: "inline" }, h("span", { class: "muted", text: "换机次数" }), quota,
              h("button", { type: "button", class: "btn small", text: "保存", onclick: function () { act("quota", { quota: quota.value }); } }),
              h("button", { type: "button", class: "btn small", text: "恢复默认", onclick: function () { act("quota", { quota: null }); } }),
              h("button", { type: "button", class: "btn small", text: "已用清零", disabled: d.rebindsUsed ? null : "",
                onclick: function () { act("quota", { reset: true }); } })))),
        h("div", { class: "section" }, h("h3", { text: "联网记录（最近 50 次）" }),
          timeline.length ? h("ul", { class: "timeline" }, timeline) : h("p", { class: "hint", text: "还没有联网记录。" })),
        audit.length ? h("div", { class: "section" }, h("h3", { text: "管理操作" }), h("ul", { class: "timeline" }, audit)) : null)
    ]);
    dr.hidden = false;
    $("scrim").hidden = false;
  }

  // ---------- 启动 ----------

  setInterval(function () {
    if (!$("app").hidden && document.visibilityState === "visible") load().catch(function () {});
  }, 60000);

  boot().catch(function () { showLogin(); });
})();
