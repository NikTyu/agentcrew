(() => {
  const AGENTS = {
    orchestrator: { name: "Оркестратор", icon: "⚙" },
    planner: { name: "Планировщик", icon: "🧭" },
    researcher: { name: "Исследователь", icon: "📚" },
    analyst: { name: "Аналитик", icon: "📊" },
    writer: { name: "Редактор", icon: "✍" },
    critic: { name: "Критик", icon: "🔍" },
  };
  const TYPE_LABELS = {
    runStarted: "Запуск",
    planCreated: "План готов",
    stepStarted: "Шаг начат",
    stepCompleted: "Шаг завершён",
    agentThinking: "Думает",
    toolCall: "Вызов инструмента",
    agentMessage: "Ответ агента",
    criticReview: "Рецензия",
    finalAnswer: "Итоговый ответ",
    error: "Ошибка",
    runCompleted: "Готово",
  };

  const $ = (sel) => document.querySelector(sel);
  const el = (tag, cls, html) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (html !== undefined) e.innerHTML = html;
    return e;
  };
  const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
  const fmt = (n) => Number(n || 0).toLocaleString("ru-RU");
  const badge = (agent) => {
    const a = AGENTS[agent] || { name: agent, icon: "•" };
    const color = AGENTS[agent] ? `var(--${agent})` : "var(--muted)";
    return `<span class="agent-badge" style="--agent-color:${color}">${a.icon} ${esc(a.name)}</span>`;
  };

  const messages = $("#messages");
  const input = $("#input");
  const send = $("#send");
  const history = [];
  let running = false;
  let run = null;

  // ---------- Вкладки ----------
  document.querySelectorAll(".tab").forEach((tab) =>
    tab.addEventListener("click", () => selectTab(tab.dataset.tab)));

  function selectTab(name) {
    document.querySelectorAll(".tab").forEach((t) => t.classList.toggle("active", t.dataset.tab === name));
    document.querySelectorAll(".panel").forEach((p) => p.classList.toggle("active", p.id === `panel-${name}`));
  }

  // ---------- Информация о системе ----------
  fetch("/api/info").then((r) => r.json()).then((info) => {
    const provider = $("#provider");
    provider.textContent = info.provider;
    provider.classList.toggle("demo", info.demo);
    provider.title = info.demo
      ? "LLM не настроена: агенты работают по офлайн-сценарию, но инструменты и данные — настоящие. Укажите Llm:ApiKey, чтобы подключить модель."
      : "Подключена LLM через OpenAI-совместимый API";

    const team = $("#panel-team");
    team.append(el("div", "team-note",
      `База знаний: ${info.knowledge.documents.length} документов (${info.knowledge.chunks} фрагментов). ` +
      `Данные продаж: ${info.sales.records} записей, кварталы ${info.sales.quarters.join(", ")}.`));
    for (const a of info.agents) {
      const card = el("div", "agent-card");
      card.innerHTML = `${badge(a.id)}<p>${esc(a.description)}</p>` +
        (a.tools.length
          ? `<div class="tool-list">${a.tools.map((t) => `<span class="tool-name" title="${esc(t.description)}">${esc(t.name)}</span>`).join("")}</div>`
          : `<div class="dur">без инструментов</div>`);
      team.append(card);
    }
  }).catch(() => { $("#provider").textContent = "сервер недоступен"; });

  // ---------- Отправка ----------
  $("#examples").addEventListener("click", (e) => {
    if (e.target.tagName === "BUTTON" && !running) {
      input.value = e.target.textContent;
      submit();
    }
  });
  $("#composer").addEventListener("submit", (e) => { e.preventDefault(); submit(); });
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); submit(); }
  });

  async function submit() {
    const task = input.value.trim();
    if (!task || running) return;
    running = true;
    send.disabled = true;
    input.value = "";
    $("#welcome")?.remove();

    messages.append(el("div", "msg user", esc(task)));
    const bubble = el("div", "msg assistant");
    const status = el("div", "status", `<span class="spinner"></span><span>Планировщик составляет план…</span>`);
    bubble.append(status);
    messages.append(bubble);
    scrollDown();

    run = { bubble, status, steps: {}, toolEntries: {}, started: Date.now(), answer: "" };
    resetInspector();

    try {
      const response = await fetch("/api/crew/run", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ task, history }),
      });
      if (!response.ok || !response.body) {
        const err = await response.json().catch(() => ({}));
        throw new Error(err.error || `HTTP ${response.status}`);
      }
      await readStream(response.body, handleEvent);
      if (run.answer) {
        history.push({ role: "user", content: task }, { role: "assistant", content: run.answer });
      }
    } catch (e) {
      showError(e.message);
    } finally {
      run.status?.remove();
      running = false;
      send.disabled = false;
      input.focus();
    }
  }

  async function readStream(body, onEvent) {
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let idx;
      while ((idx = buffer.indexOf("\n\n")) >= 0) {
        const chunk = buffer.slice(0, idx);
        buffer = buffer.slice(idx + 2);
        const data = chunk.split("\n").filter((l) => l.startsWith("data:")).map((l) => l.slice(5).trim()).join("");
        if (data) onEvent(JSON.parse(data));
      }
    }
  }

  // ---------- Обработка событий ----------
  function handleEvent(ev) {
    switch (ev.type) {
      case "planCreated": renderPlan(ev.data); setStatus("Исполнители работают над шагами…"); break;
      case "stepStarted": setStep(ev.stepId, "running", "выполняется"); break;
      case "stepCompleted": setStep(ev.stepId, ev.data?.succeeded ? "done" : "failed", ev.data?.succeeded ? "готово" : "ошибка"); break;
      case "toolCall": setStatus(`${AGENTS[ev.agent]?.name ?? ev.agent} вызывает ${ev.data?.tool}…`); break;
      case "criticReview": renderReview(ev.data); setStatus(ev.data?.approved ? "Критик одобрил ответ" : "Критик вернул ответ на доработку…"); break;
      case "agentThinking":
        if (ev.agent === "writer") setStatus("Редактор пишет ответ…");
        if (ev.agent === "critic") setStatus("Критик проверяет черновик…");
        break;
      case "finalAnswer": renderAnswer(ev.text); break;
      case "runCompleted": renderMetrics(ev.data); break;
      case "error": if (ev.agent === "orchestrator") showError(ev.text); break;
    }
    appendTrace(ev);
  }

  function setStatus(text) {
    if (run?.status) run.status.lastElementChild.textContent = text;
  }

  function showError(message) {
    run.bubble.classList.add("error");
    run.bubble.append(el("div", "", `Ошибка: ${esc(message)}`));
  }

  function renderAnswer(text) {
    run.answer = text;
    run.status?.remove();
    run.status = null;
    const md = el("div", "md", window.renderMarkdown(text));
    run.bubble.prepend(md);
    scrollDown();
  }

  // ---------- План ----------
  function resetInspector() {
    $("#panel-plan").innerHTML = `<div class="empty"><span class="spinner" style="display:inline-block"></span></div>`;
    $("#panel-trace").innerHTML = "";
    $("#panel-metrics").innerHTML = `<div class="empty">Идёт выполнение…</div>`;
    $("#traceCount").textContent = "0";
    selectTab("plan");
  }

  function computeWaves(steps) {
    const done = new Set();
    const waves = [];
    let pending = [...steps];
    while (pending.length) {
      let wave = pending.filter((s) => (s.dependsOn || []).every((d) => done.has(d)));
      if (!wave.length) wave = [pending[0]];
      wave.forEach((s) => done.add(s.id));
      pending = pending.filter((s) => !wave.includes(s));
      waves.push(wave);
    }
    return waves;
  }

  function renderPlan(plan) {
    const panel = $("#panel-plan");
    panel.innerHTML = "";
    panel.append(el("div", "plan-goal", esc(plan.goal)));
    const steps = plan.steps || [];
    panel.append(el("div", "plan-hint", steps.length
      ? `${steps.length} шаг(ов). Шаги одной волны выполняются параллельно.`
      : "Простой запрос — исполнители не нужны, отвечает редактор."));

    const waves = el("div", "waves");
    computeWaves(steps).forEach((wave, i) => {
      if (i > 0) waves.append(el("div", "wave-arrow", "↓"));
      const block = el("div");
      block.append(el("div", "wave-label", `Волна ${i + 1}${wave.length > 1 ? " · параллельно" : ""}`));
      const row = el("div", "wave-steps");
      for (const s of wave) {
        const card = el("div", "step");
        card.style.setProperty("--agent-color", `var(--${s.agent})`);
        card.innerHTML = `<div class="step-head">${badge(s.agent)}<span class="step-id">${esc(s.id)}</span><span class="pill">ожидает</span></div>
          <div class="step-task">${esc(s.task)}</div>
          ${s.dependsOn?.length ? `<div class="step-deps">зависит от: ${s.dependsOn.map(esc).join(", ")}</div>` : ""}`;
        run.steps[s.id] = card.querySelector(".pill");
        row.append(card);
      }
      block.append(row);
      waves.append(block);
    });
    if (steps.length) waves.append(el("div", "wave-arrow", "↓"));
    const finale = el("div", "stage-row", steps.length
      ? `${badge("writer")}<span class="dur">⇄</span>${badge("critic")}<span class="dur">цикл «черновик → рецензия → доработка»</span>`
      : `${badge("writer")}<span class="dur">отвечает напрямую, без проверки критиком</span>`);
    waves.append(finale);
    panel.append(waves);
    const reviews = el("div");
    reviews.id = "reviews";
    panel.append(reviews);
  }

  function setStep(id, cls, label) {
    const pill = run.steps[id];
    if (!pill) return;
    pill.className = `pill ${cls}`;
    pill.textContent = label;
  }

  function renderReview(v) {
    const box = $("#reviews");
    if (!box) return;
    const card = el("div", `review ${v.approved ? "ok" : "bad"}`);
    card.innerHTML = `<div class="event-head">${badge("critic")}<strong>${v.approved ? "Одобрено" : "На доработку"}</strong><span class="pill">оценка ${v.score}/10</span></div>
      ${v.feedback ? `<div class="event-text">${esc(v.feedback)}</div>` : ""}
      ${v.issues?.length ? `<ul>${v.issues.map((i) => `<li>${esc(i)}</li>`).join("")}</ul>` : ""}`;
    box.append(card);
  }

  // ---------- Трасса ----------
  function appendTrace(ev) {
    const panel = $("#panel-trace");
    const time = new Date(ev.timestamp);
    const rel = ((time - run.started) / 1000).toFixed(1);

    // Результат инструмента дописываем в карточку его вызова.
    if (ev.type === "toolResult" && run.toolEntries[ev.data?.callId]) {
      const entry = run.toolEntries[ev.data.callId];
      entry.querySelector(".dur").textContent = `${ev.data.ok ? "✓" : "✗"} ${ev.data.durationMs} мс`;
      const details = el("details", "", `<summary>Результат (${fmt(ev.text?.length)} симв.)</summary><pre>${esc(ev.text)}</pre>`);
      entry.querySelector(".event-body").append(details);
      return;
    }
    if (ev.type === "agentThinking" && /^Итерация \d+$/.test(ev.text || "")) return;

    const row = el("div", `event ev-${ev.type}`);
    let body = "";
    if (ev.type === "toolCall") {
      body = `<span class="tool-name">${esc(ev.data?.tool)}</span> <span class="dur">…</span>
        <details><summary>Аргументы</summary><pre>${esc(JSON.stringify(ev.data?.arguments ?? {}, null, 2))}</pre></details>`;
    } else if (ev.type === "planCreated") {
      body = `<div class="event-text">${esc(ev.text)}</div><details><summary>JSON плана</summary><pre>${esc(JSON.stringify(ev.data, null, 2))}</pre></details>`;
    } else if (ev.type === "criticReview") {
      body = `<div class="event-text">${ev.data.approved ? "✓ одобрено" : "↺ на доработку"} · ${ev.data.score}/10 — ${esc(ev.text)}</div>`;
    } else if (ev.type === "runCompleted") {
      body = `<div class="event-text">${(ev.data.durationMs / 1000).toFixed(1)} с · ${ev.data.totals.llmCalls} обращений к LLM · ${ev.data.totals.toolCalls} вызовов инструментов</div>`;
    } else if (ev.type === "finalAnswer" || ev.type === "agentMessage") {
      const long = (ev.text || "").length > 280;
      body = long
        ? `<div class="event-text clamp">${esc(ev.text)}</div><details><summary>Полностью</summary><pre>${esc(ev.text)}</pre></details>`
        : `<div class="event-text">${esc(ev.text)}</div>`;
    } else if (ev.text) {
      body = `<div class="event-text">${esc(ev.text)}</div>`;
    }

    row.innerHTML = `<div class="event-time">+${rel}s</div>
      <div class="event-body"><div class="event-head">${badge(ev.agent)}<span class="event-type">${TYPE_LABELS[ev.type] ?? ev.type}${ev.stepId ? ` · ${esc(ev.stepId)}` : ""}</span></div>${body}</div>`;
    if (ev.type === "toolCall") run.toolEntries[ev.data?.callId] = row;
    panel.append(row);
    $("#traceCount").textContent = String(panel.children.length);
    panel.scrollTop = panel.scrollHeight;
  }

  // ---------- Метрики ----------
  function renderMetrics(d) {
    const t = d.totals;
    $("#panel-metrics").innerHTML = `
      <div class="kpis">
        ${kpi((d.durationMs / 1000).toFixed(1) + " с", "время прогона")}
        ${kpi(t.llmCalls, "обращений к LLM")}
        ${kpi(t.toolCalls, "вызовов инструментов")}
        ${kpi(d.revisions, "доработок по критике")}
        ${kpi(fmt(t.inputTokens), "входных токенов")}
        ${kpi(fmt(t.outputTokens), "выходных токенов")}
      </div>
      <table class="usage">
        <thead><tr><th>Агент</th><th>LLM</th><th>Инструменты</th><th>Вход, ток.</th><th>Выход, ток.</th></tr></thead>
        <tbody>${d.usage.map((u) => `<tr><td>${badge(u.agent)}</td><td>${u.llmCalls}</td><td>${u.toolCalls}</td><td>${fmt(u.inputTokens)}</td><td>${fmt(u.outputTokens)}</td></tr>`).join("")}</tbody>
      </table>`;
    run.bubble.append(el("div", "msg-meta",
      `<span>⏱ ${(d.durationMs / 1000).toFixed(1)} с</span><span>🧠 ${t.llmCalls} LLM</span><span>🛠 ${t.toolCalls} инструментов</span><span>↺ ${d.revisions} доработок</span>` +
      (d.approved ? "" : `<span class="unverified" title="Последняя версия ответа содержит замечания критика или автопроверки — см. вкладку «План»">⚠ проверку прошёл не полностью</span>`)));
    scrollDown();
  }

  const kpi = (value, label) => `<div class="kpi"><div class="kpi-value">${value}</div><div class="kpi-label">${label}</div></div>`;

  function scrollDown() {
    messages.scrollTop = messages.scrollHeight;
  }
})();
