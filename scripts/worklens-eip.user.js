// ==UserScript==
// @name         WorkLens 複製到 EIP
// @namespace    https://github.com/wengct/WorkLens
// @version      1.5.0
// @description  從 WorkLens 多專案工作摘要開啟指定日期的新版 EIP、清除舊明細並填入專案、類型、摘要指定的工時與說明；絕不自動送出。
// @match        http://127.0.0.1/*
// @match        http://localhost/*
// @match        https://duotify-eip.azurewebsites.net/staff/on-duty-report/create*
// @match        https://duotify-eip.azurewebsites.net/staff/on-duty-report/edit/*
// @run-at       document-idle
// @grant        GM_setValue
// @grant        GM_getValue
// @grant        GM_deleteValue
// @grant        GM_openInTab
// @noframes
// @author       Chenting
// @license      MIT
// ==/UserScript==

(function () {
  "use strict";

  const CREATE_URL = "https://duotify-eip.azurewebsites.net/staff/on-duty-report/create";
  const PENDING_KEY = "worklens-eip-pending-import-v5";
  const BUTTON_ID = "worklens-copy-to-eip";
  const STATUS_ID = "worklens-eip-status";
  const PAYLOAD_MAX_AGE_MS = 30 * 60 * 1000;
  const WORK_TYPES = ["專案管理", "UIUX相關", "需求評估", "功能開發", "功能測試", "BUG處理", "文件相關", "客服", "其他"];

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const normalize = (value) => String(value ?? "").replace(/\s+/g, " ").trim();
  const log = (...args) => console.log("[worklens-eip]", ...args);
  const warn = (...args) => console.warn("[worklens-eip]", ...args);

  function normalizeCategory(value) {
    return normalize(value).replace(/[\s_\-／/]+/g, "").toUpperCase();
  }

  function normalizeDate(value) {
    const match = normalize(value).match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$/);
    if (!match) return "";
    const [year, month, day] = match.slice(1).map(Number);
    const date = new Date(Date.UTC(year, month - 1, day));
    if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return "";
    return `${String(year).padStart(4, "0")}/${String(month).padStart(2, "0")}/${String(day).padStart(2, "0")}`;
  }

  function toUrlDate(value) {
    return normalizeDate(value).replaceAll("/", "-");
  }

  function normalizeWorkHours(value) {
    if (value === "" || value === null || value === undefined) return 0;
    const hours = Number(value);
    if (!Number.isFinite(hours) || hours < 0 || hours > 24 || !Number.isInteger(hours * 2)) {
      throw new Error("工時必須是 0 到 24 之間、以 0.5 為級距的數字。");
    }
    return hours;
  }

  function parseSectionHeading(headingText) {
    const match = headingText.match(/^(.*?)\s*[：:]\s*(.*?)$/);
    if (!match) return { heading: headingText, hours: 0 };

    const heading = normalize(match[1]);
    const rawHours = normalize(match[2]);
    if (!heading || !rawHours) throw new Error(`工作分類格式無效：「${headingText}」`);
    try {
      return { heading, hours: normalizeWorkHours(rawHours) };
    } catch (error) {
      throw new Error(`「${heading}」的工時無效：${error.message}`);
    }
  }

  function createTransferToken() {
    if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  }

  function getImportTokenFromUrl() {
    return new URL(location.href).searchParams.get("worklensImport") || "";
  }

  function removeImportTokenFromUrl() {
    const url = new URL(location.href);
    url.searchParams.delete("worklensImport");
    history.replaceState(history.state, "", url.href);
  }

  function getEipPageDate() {
    const url = new URL(location.href);
    const queryDate = normalizeDate(url.searchParams.get("date"));
    if (queryDate) return queryDate;
    const routeMatch = url.pathname.match(/\/staff\/on-duty-report\/edit\/(\d{4}-\d{2}-\d{2})\/?$/);
    return normalizeDate(routeMatch?.[1]);
  }

  function parseWorkLensMarkdown(rawText) {
    const raw = String(rawText ?? "").replace(/\r\n?/g, "\n");
    if (!raw.trim()) throw new Error("工作摘要是空的，請先產生或填寫摘要。");

    const dateMatch = raw.match(/^##\s+日期\s*[：:]\s*(\d{4}[-/]\d{1,2}[-/]\d{1,2})\s*$/im)
      || raw.match(/^#\s+每日工作回報(?:\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}))?\s*$/im);
    const date = normalizeDate(dateMatch?.[1]);
    if (!date) throw new Error("找不到有效的「## 日期：YYYY-MM-DD」，只能匯入每日摘要。");

    const knownTypes = new Map(WORK_TYPES.map((type) => [normalizeCategory(type), type]));
    const sections = [];
    const lines = raw.split("\n");
    let currentProject = "";
    let insideWorkItems = false;
    let currentSection = null;
    let projectHeadingCount = 0;

    const flushSection = () => {
      if (!currentSection) return;
      const description = currentSection.lines.join("\n").trim();
      if (description) {
        sections.push({
          project: currentSection.project,
          heading: currentSection.heading,
          hours: currentSection.hours,
          description
        });
      }
      currentSection = null;
    };

    for (const line of lines) {
      const heading = line.match(/^(#{1,6})\s+(.+?)\s*#*\s*$/);
      if (heading) {
        const level = heading[1].length;
        const headingText = normalize(heading[2]);

        if (level <= 2 || (level === 3 && insideWorkItems)) flushSection();
        if (level === 2) {
          const projectMatch = headingText.match(/^專案\s*[：:]\s*(.+)$/i);
          if (projectMatch) {
            currentProject = normalize(projectMatch[1]);
            insideWorkItems = false;
            projectHeadingCount += 1;
          } else if (/^工作項目$/i.test(headingText)) {
            if (!currentProject) throw new Error("「## 工作項目」前缺少「## 專案：專案名稱」。");
            insideWorkItems = true;
          } else {
            insideWorkItems = false;
          }
        } else if (level === 3 && insideWorkItems) {
          const sectionHeading = parseSectionHeading(headingText);
          currentSection = {
            project: currentProject,
            heading: sectionHeading.heading,
            hours: sectionHeading.hours,
            lines: []
          };
        }
      } else if (currentSection) {
        currentSection.lines.push(line.replace(/[ \t]+$/g, ""));
      }
    }
    flushSection();

    if (!projectHeadingCount) {
      throw new Error("找不到「## 專案：專案名稱」，無法自動選擇 EIP 專案。");
    }

    const grouped = new Map();
    for (const section of sections) {
      const matchedType = knownTypes.get(normalizeCategory(section.heading));
      const type = matchedType || "其他";
      const description = matchedType
        ? section.description
        : `【原分類：${section.heading}】\n${section.description}`;
      const key = JSON.stringify([section.project, type]);
      if (!grouped.has(key)) grouped.set(key, { project: section.project, type, hours: 0, descriptions: [] });
      const groupedItem = grouped.get(key);
      groupedItem.hours = normalizeWorkHours(groupedItem.hours + section.hours);
      groupedItem.descriptions.push(description);
    }

    const items = Array.from(grouped.values()).map((item) => ({
      project: item.project,
      type: item.type,
      hours: item.hours,
      description: item.descriptions.join("\n\n")
    }));
    if (!items.length) throw new Error("各「## 工作項目」下找不到可匯入的「### 工作分類」與說明。");

    return { date, items };
  }

  function showWorkLensStatus(message, failed = false) {
    let status = document.getElementById(STATUS_ID);
    const actions = document.querySelector(".report-workspace .report-primary-actions");
    if (!actions) return;
    if (!status) {
      status = document.createElement("span");
      status.id = STATUS_ID;
      status.style.cssText = "align-self:center;font-size:12px;color:#65728a;";
      actions.appendChild(status);
    }
    status.style.color = failed ? "#b42318" : "#65728a";
    status.textContent = message;
  }

  function installWorkLensButton() {
    if (location.pathname !== "/reports") return;
    const actions = document.querySelector(".report-workspace .report-primary-actions");
    const reportBody = document.querySelector(".report-workspace textarea.report-body");
    if (!actions || !reportBody || document.getElementById(BUTTON_ID)) return;

    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    button.className = "button button-secondary button-small";
    button.textContent = "複製到 EIP";
    button.addEventListener("click", async () => {
      button.disabled = true;
      showWorkLensStatus("正在準備 EIP 明細…");
      try {
        const source = document.querySelector(".report-workspace textarea.report-body");
        const draft = parseWorkLensMarkdown(source?.value);
        const token = createTransferToken();
        await GM_setValue(PENDING_KEY, {
          version: 5,
          token,
          createdAt: Date.now(),
          sourceUrl: location.href,
          draft
        });
        const targetUrl = new URL(CREATE_URL);
        targetUrl.searchParams.set("date", toUrlDate(draft.date));
        targetUrl.searchParams.set("worklensImport", token);
        GM_openInTab(targetUrl.href, { active: true, insert: true, setParent: true });
        showWorkLensStatus("已開啟指定日期的 EIP，正在自動填入。");
      } catch (error) {
        warn("Preparing import failed", error);
        showWorkLensStatus(error?.message || String(error), true);
      } finally {
        button.disabled = false;
      }
    });
    actions.appendChild(button);
  }

  function bootWorkLens() {
    installWorkLensButton();
    const observer = new MutationObserver(installWorkLensButton);
    observer.observe(document.body, { childList: true, subtree: true });
    window.addEventListener("popstate", installWorkLensButton);
  }

  function visible(element) {
    if (!element) return false;
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return style.visibility !== "hidden" && style.display !== "none" && rect.width > 0 && rect.height > 0;
  }

  function setNativeValue(element, value) {
    const prototype = element instanceof HTMLTextAreaElement
      ? HTMLTextAreaElement.prototype
      : HTMLInputElement.prototype;
    const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
    if (descriptor?.set) descriptor.set.call(element, String(value ?? ""));
    else element.value = String(value ?? "");
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
    element.dispatchEvent(new Event("blur", { bubbles: true }));
  }

  async function waitFor(getter, label, timeout = 10000) {
    const startedAt = Date.now();
    while (Date.now() - startedAt < timeout) {
      const result = getter();
      if (result) return result;
      await sleep(100);
    }
    throw new Error(`找不到 ${label}`);
  }

  function getRows() {
    return Array.from(document.querySelectorAll("main .entry"))
      .filter((row) => row.querySelector("input[aria-label='工時']") && row.querySelector("textarea[aria-label='說明']"));
  }

  function findButtonByText(text) {
    return Array.from(document.querySelectorAll("button"))
      .find((button) => {
        if (button.closest(`#${STATUS_ID}`)) return false;
        const label = normalize(`${button.innerText} ${button.getAttribute("aria-label")} ${button.title}`);
        return label.includes(text);
      });
  }

  function findDeleteButton(row) {
    return row.querySelector("button[aria-label='刪除此列']");
  }

  async function waitForEipReady() {
    await waitFor(() => findButtonByText("登出"), "EIP 登入資料", 20000);
    await waitFor(() => findButtonByText("其他專案"), "「其他專案…」按鈕", 20000);

    let previousCount = -1;
    let stableSince = Date.now();
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
      const count = getRows().length;
      if (count !== previousCount) {
        previousCount = count;
        stableSince = Date.now();
      }
      if (Date.now() - stableSince >= 800) return;
      await sleep(100);
    }
    throw new Error("EIP 既有明細尚未載入完成");
  }

  async function deleteRow(row) {
    const button = findDeleteButton(row);
    if (!button || button.disabled) throw new Error("找不到可用的刪除明細按鈕");
    const before = getRows().length;
    button.click();
    await waitFor(() => getRows().length < before, "已刪除的工作明細消失", 5000);
  }

  async function clearExistingRows() {
    while (getRows().length > 0) {
      await deleteRow(getRows()[getRows().length - 1]);
    }
  }

  function findQuickProjectButton(project) {
    const wanted = normalize(project);
    const buttons = Array.from(document.querySelectorAll(".quick-add button.quick-chip:not(.quick-chip--ghost)"));
    return buttons.find((button) => normalize(button.title) === wanted || normalize(button.innerText) === wanted)
      || buttons.find((button) => normalize(button.title).includes(wanted) || normalize(button.innerText).includes(wanted));
  }

  async function selectMatOption(matSelect, optionText) {
    matSelect.click();
    await waitFor(() => document.querySelector(".cdk-overlay-pane mat-option"), "下拉選單");

    const searchInput = Array.from(document.querySelectorAll(".cdk-overlay-pane input[aria-label='dropdown search']"))
      .find((input) => visible(input) && !input.disabled);
    if (searchInput) {
      setNativeValue(searchInput, optionText);
      await sleep(450);
    }

    const wanted = normalize(optionText);
    const options = Array.from(document.querySelectorAll(".cdk-overlay-pane mat-option"));
    const option = options.find((item) => normalize(item.innerText) === wanted)
      || options.find((item) => normalize(item.innerText).includes(wanted));
    if (!option) {
      document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
      throw new Error(`找不到 EIP 下拉選項：${optionText}`);
    }
    option.click();
    await sleep(350);
  }

  async function addRowForProject(project) {
    const before = getRows().length;
    const quickButton = findQuickProjectButton(project);
    if (quickButton) {
      quickButton.click();
    } else {
      const addButton = await waitFor(() => findButtonByText("其他專案"), "「其他專案…」按鈕");
      addButton.click();
    }

    const row = await waitFor(() => getRows().length > before && getRows()[before], "新增的工作明細", 5000);
    if (!quickButton) {
      const projectSelect = await waitFor(
        () => row.querySelector("mat-select[aria-label='專案']"),
        `第 ${before + 1} 筆專案欄位`
      );
      await selectMatOption(projectSelect, project);
    }
    return await waitFor(() => getRows()[before], `第 ${before + 1} 筆工作明細`);
  }

  async function fillRow(item, index) {
    let row = await addRowForProject(item.project);
    let typeSelect = row.querySelector("mat-select[aria-label='類型']");
    if (!typeSelect) throw new Error(`第 ${index + 1} 筆找不到工作類型欄位`);
    await selectMatOption(typeSelect, item.type);

    row = await waitFor(() => getRows()[index], `第 ${index + 1} 筆工作明細`);
    const hours = row.querySelector("input[aria-label='工時']");
    const description = row.querySelector("textarea[aria-label='說明']");
    if (!hours || !description) throw new Error(`第 ${index + 1} 筆找不到工時或說明欄位`);
    setNativeValue(hours, String(item.hours));
    setNativeValue(description, item.description);
  }

  function installSubmissionGuard() {
    document.addEventListener("submit", (event) => {
      if (window.__workLensEipImporting) {
        event.preventDefault();
        event.stopImmediatePropagation();
        warn("匯入進行中，已阻止表單送出。");
      }
    }, true);
    document.addEventListener("click", (event) => {
      const submitter = event.target.closest("button[type='submit'], input[type='submit']");
      if (submitter && window.__workLensEipImporting) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    }, true);
  }

  function showEipStatus(message, failed = false, onRetry = null) {
    let panel = document.getElementById(STATUS_ID);
    if (!panel) {
      panel = document.createElement("div");
      panel.id = STATUS_ID;
      panel.style.cssText = "position:fixed;right:18px;bottom:18px;z-index:2147483647;max-width:min(460px,calc(100vw - 36px));padding:12px 14px;border:1px solid #d0d7de;border-radius:8px;background:#fff;color:#24292f;box-shadow:0 12px 32px rgba(0,0,0,.18);font:13px/1.5 -apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;white-space:pre-wrap;";
      document.body.appendChild(panel);
    }
    panel.replaceChildren();
    panel.style.borderColor = failed ? "#f0a8a8" : "#d0d7de";
    panel.append(document.createTextNode(message));
    if (onRetry) {
      const retry = document.createElement("button");
      retry.type = "button";
      retry.textContent = "重試匯入";
      retry.style.cssText = "display:block;margin-top:8px;padding:6px 10px;cursor:pointer;";
      retry.addEventListener("click", onRetry, { once: true });
      panel.appendChild(retry);
    }
  }

  async function importPendingDraft() {
    if (window.__workLensEipImporting) return;

    const payload = await GM_getValue(PENDING_KEY, null);
    if (!payload) return;
    if (!payload.createdAt || Date.now() - payload.createdAt > PAYLOAD_MAX_AGE_MS) {
      await GM_deleteValue(PENDING_KEY);
      return;
    }

    const importToken = getImportTokenFromUrl();
    if (importToken && (!payload.token || payload.token !== importToken)) {
      showEipStatus("這個 EIP 分頁的匯入識別碼已失效，未對表單進行任何操作。", true);
      return;
    }

    const { draft } = payload;
    const pageDate = getEipPageDate();
    if (!pageDate || pageDate !== normalizeDate(draft?.date)) return;

    window.__workLensEipImporting = true;
    showEipStatus("正在等待 EIP 載入並填入 WorkLens 摘要…");
    try {
      if (!draft?.date || !draft?.items?.length || draft.items.some((item) => (
        !item?.project
        || !item?.type
        || !item?.description
        || normalizeWorkHours(item?.hours) !== item.hours
      ))) {
        throw new Error("待匯入資料不完整。");
      }
      await waitForEipReady();
      await clearExistingRows();
      for (let index = 0; index < draft.items.length; index += 1) {
        await fillRow(draft.items[index], index);
      }
      await GM_deleteValue(PENDING_KEY);
      removeImportTokenFromUrl();
      const totalHours = draft.items.reduce((sum, item) => sum + item.hours, 0);
      showEipStatus(`已填入 ${draft.items.length} 筆明細，共 ${totalHours} 小時。\n未指定工時的分類已填入 0。\n請檢查並調整工時後手動按「建立」或「更新」；腳本不會自動送出。`);
    } catch (error) {
      warn("Import failed", error);
      showEipStatus(`匯入失敗：${error?.message || String(error)}\n表單未送出。`, true, importPendingDraft);
    } finally {
      window.__workLensEipImporting = false;
    }
  }

  async function bootEip() {
    installSubmissionGuard();
    await importPendingDraft();
  }

  window.__workLensEipTestApi = {
    getEipPageDate,
    normalizeDate,
    normalizeWorkHours,
    parseWorkLensMarkdown,
    parseSectionHeading,
    toUrlDate,
    workTypes: [...WORK_TYPES]
  };
  if (window.__WORKLENS_EIP_TEST__) return;
  const isEip = location.hostname === "duotify-eip.azurewebsites.net";
  if (isEip) bootEip();
  else bootWorkLens();
})();
