// Минимальный безопасный рендерер Markdown (без зависимостей): заголовки, списки,
// таблицы, цитаты, код, жирный/курсив. HTML во входе всегда экранируется.
(function () {
  const escape = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

  function inline(text) {
    return escape(text)
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/(^|[^*])\*([^*\s][^*]*)\*/g, "$1<em>$2</em>");
  }

  const isNumeric = (s) => /^[+\-−]?[\d\s  .,]+(%|₽)?$/.test(s.replace(/<\/?strong>/g, "").trim());

  function table(lines) {
    const rows = lines
      .filter((l, i) => !(i === 1 && /^\|?\s*:?-{2,}/.test(l)))
      .map((l) => l.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map((c) => inline(c.trim())));
    const [head, ...body] = rows;
    const cell = (tag, c) => `<${tag}${isNumeric(c) ? ' class="num"' : ""}>${c}</${tag}>`;
    return `<table><thead><tr>${head.map((c) => cell("th", c)).join("")}</tr></thead>` +
      `<tbody>${body.map((r) => `<tr>${r.map((c) => cell("td", c)).join("")}</tr>`).join("")}</tbody></table>`;
  }

  function render(src) {
    const lines = (src || "").replace(/\r/g, "").split("\n");
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (/^```/.test(line)) {
        const code = [];
        i++;
        while (i < lines.length && !/^```/.test(lines[i])) code.push(lines[i++]);
        i++;
        out.push(`<pre><code>${escape(code.join("\n"))}</code></pre>`);
        continue;
      }
      const h = line.match(/^(#{1,4})\s+(.*)$/);
      if (h) {
        out.push(`<h${h[1].length}>${inline(h[2])}</h${h[1].length}>`);
        i++;
        continue;
      }
      if (/^\s*\|/.test(line)) {
        const block = [];
        while (i < lines.length && /^\s*\|/.test(lines[i])) block.push(lines[i++]);
        out.push(table(block));
        continue;
      }
      if (/^>\s?/.test(line)) {
        const block = [];
        while (i < lines.length && /^>\s?/.test(lines[i])) block.push(lines[i++].replace(/^>\s?/, ""));
        out.push(`<blockquote>${inline(block.join(" "))}</blockquote>`);
        continue;
      }
      const list = line.match(/^\s*([-*]|\d+[.)])\s+/);
      if (list) {
        const ordered = /\d/.test(list[1]);
        const items = [];
        const isItem = (l) => /^\s*([-*]|\d+[.)])\s+/.test(l ?? "");
        while (i < lines.length && isItem(lines[i])) {
          items.push(`<li>${inline(lines[i].replace(/^\s*([-*]|\d+[.)])\s+/, ""))}</li>`);
          i++;
          // Модели часто разделяют пункты пустыми строками — это всё ещё один список, а не несколько «1.».
          let j = i;
          while (j < lines.length && !lines[j].trim()) j++;
          if (j > i && isItem(lines[j]) && /\d/.test(lines[j].match(/^\s*([-*]|\d+[.)])/)[1]) === ordered) i = j;
        }
        out.push(ordered ? `<ol>${items.join("")}</ol>` : `<ul>${items.join("")}</ul>`);
        continue;
      }
      if (!line.trim()) {
        i++;
        continue;
      }
      const para = [];
      while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|```|\s*\||>|\s*([-*]|\d+[.)])\s+)/.test(lines[i])) {
        para.push(lines[i++]);
      }
      out.push(`<p>${inline(para.join(" "))}</p>`);
    }
    return out.join("\n");
  }

  window.renderMarkdown = render;
})();
