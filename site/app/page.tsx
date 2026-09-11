import {
  ArrowRight,
  Bot,
  CalendarDays,
  Check,
  Code2,
  Database,
  Eye,
  FileSearch,
  FileText,
  GitCommitHorizontal,
  GitFork,
  HardDrive,
  MessageSquareText,
  PenLine,
  Search,
  ShieldCheck,
  Sparkles,
  Terminal,
} from 'lucide-react';

import { InstallCommand } from '@/components/install-command';

export const dynamic = 'force-static';

const windowsCommand = 'irm https://raw.githubusercontent.com/wengct/WorkLens/main/scripts/get.ps1 | iex';
const macCommand = 'curl -fsSL https://raw.githubusercontent.com/wengct/WorkLens/main/scripts/get.sh | bash';
const assetPrefix = process.env.NEXT_PUBLIC_BASE_PATH ?? '';

const supportedSources = [
  { name: 'Azure DevOps Services', image: 'azure-devops.svg' },
  { name: 'Git', image: 'git.svg' },
  { name: 'Codex', image: 'codex-color.svg' },
  { name: 'Claude Code', image: 'claudecode-color.svg' },
  { name: 'GitHub Copilot', image: 'githubcopilot.svg' },
];

function Brand() {
  return (
    <a className="brand" href="#top" aria-label="WorkLens 首頁">
      <img src={`${assetPrefix}/brand-mark.png`} alt="" width="38" height="38" />
      <span><strong>WorkLens</strong><small>工作摘要與回報</small></span>
    </a>
  );
}

const traces = [
  { icon: GitCommitHorizontal, type: 'GIT COMMIT', text: '程式碼改變的軌跡', time: '09:42' },
  { icon: MessageSquareText, type: 'CODING AGENT SESSION', text: '來回推敲的思考脈絡', time: '11:18' },
  { icon: Search, type: 'ISSUE TRACE', text: '反覆追查才找到的答案', time: '14:07' },
  { icon: PenLine, type: 'MANUAL NOTE', text: '會議、決定與未寫下的片刻', time: '16:35' },
];

export default function Home() {
  return (
    <main id="top">
      <header className="site-header page-width">
        <Brand />
        <nav aria-label="主要導覽">
          <a href="#story">為什麼</a>
          <a href="#product">如何運作</a>
          <a href="#workflow">持續回報</a>
          <a href="#ai-assistance">AI 聚焦</a>
          <a href="#sensitive-protection">機敏防護</a>
          <a href="#privacy">Local-first</a>
        </nav>
        <a className="header-action" href="#install">下載使用 <ArrowRight size={16} /></a>
      </header>

      <nav className="section-dock" aria-label="快速章節導覽">
        <a href="#story"><span>01</span><strong>散落的痕跡</strong></a>
        <a href="#product"><span>02</span><strong>重新聚焦</strong></a>
        <a href="#workflow"><span>03</span><strong>持續回報</strong></a>
        <a href="#ai-assistance"><span>04</span><strong>AI 聚焦引擎</strong></a>
        <a href="#sensitive-protection"><span>05</span><strong>機敏資訊防護</strong></a>
        <a href="#privacy"><span>06</span><strong>Local-first</strong></a>
      </nav>

      <section className="hero page-width">
        <div className="hero-index" aria-hidden="true">WORK / 001</div>
        <div className="hero-main">
          <p className="kicker"><span /> A LENS FOR YOUR WORKDAY</p>
          <h1><span className="heading-line">每日的辛勞，</span><span className="heading-line">其實從未真正消失。</span></h1>
          <p className="hero-intro">它們只是安靜地沉澱在你的設備裡，<br />等待有一天，再次被看見。</p>
        </div>
        <div className="hero-aside">
          <div className="focus-mark" aria-hidden="true"><span /><span /><span /><span /></div>
          <p>WorkLens 讓 AI 沿著程式碼變更、PR、AI 對話與日常紀錄留下的痕跡，辨認關聯、還原脈絡，更快找回你真正推進的工作。</p>
          <a href="#story">沿著痕跡往回走 <ArrowRight size={17} /></a>
        </div>
      </section>

      <section className="trace-section" id="story">
        <div className="page-width trace-layout">
          <div className="section-label"><span>01</span><p>散落的痕跡</p></div>
          <div className="trace-copy">
            <h2><span className="heading-line">一天，留下許多</span><span className="heading-line">沒有名字的片刻。</span></h2>
            <p>一筆 commit、一段對話、一次問題排查、一場會議、一個決定——它們散落在一天的不同角落，留下痕跡，卻未必留下名字。</p>
          </div>
          <div className="trace-list">
            {traces.map(({ icon: Icon, type, text, time }) => (
              <article key={type}>
                <span className="trace-icon"><Icon size={18} /></span>
                <div><small>{type}</small><strong>{text}</strong></div>
                <time>{time}</time>
              </article>
            ))}
          </div>
        </div>
      </section>

      <section className="question-section page-width">
        <div className="question-line"><span>一天結束</span><i /></div>
        <blockquote>
          <p><span>今天，我究竟</span><span>完成了什麼？</span></p>
        </blockquote>
        <div className="question-answer">
          <span>於是</span>
          <p>讓 AI 沿著這些痕跡往回走，找出原本容易被忽略的進展，回答那個看似簡單、卻總是難以完整說清楚的問題。</p>
        </div>
      </section>

      <section className="product-section" id="product">
        <div className="page-width">
          <div className="product-heading">
            <div className="section-label light-label"><span>02</span><p>重新聚焦</p></div>
            <div>
              <p className="overline">讓工作留下的痕跡，有了脈絡。</p>
              <h2>讓散落的工作碎片，<br />重新聚在一起。</h2>
            </div>
            <p>WorkLens 是一套以 local-first 為核心的個人工作歷程與工時回報工具。它先依日期與專案整理紀錄，再讓 AI 從零散線索中發掘工作、重建脈絡。</p>
          </div>

          <figure className="product-shot">
            <div className="shot-toolbar"><span><i /> PRODUCT VIEW</span><span>工作摘要・每日工作區</span></div>
            <img src={`${assetPrefix}/worklens-report-workspace.png`} alt="WorkLens 工作摘要工作區，顯示每日摘要、固定整理工具列與日期瀏覽歷程" width="1536" height="639" />
            <figcaption><span>01</span> 在同一個工作區完成摘要整理、AI 協助、儲存與日期瀏覽。</figcaption>
          </figure>

          <div className="product-flow">
            <article><span>01</span><Code2 size={22} /><h3>拾起</h3><p>從 Git 的程式碼變更、Azure DevOps 的 PR，到 Codex、Claude Code 與 GitHub Copilot 的對話；再由你補上會議與日常紀錄。</p></article>
            <article><span>02</span><CalendarDays size={22} /><h3>聚焦</h3><p>依日期與專案重組紀錄，透過專案與關鍵字，找回每一段工作的前因後果。</p></article>
            <article><span>03</span><Sparkles size={22} /><h3>成稿</h3><p>由 AI 將一天或一週的推進整理成完整摘要，再由你編修、匯出與回報。</p></article>
          </div>

        <section className="source-brands" aria-labelledby="source-brands-title">
          <h3 id="source-brands-title">支援的資料來源</h3>
          <ul className="source-brand-list">
            {supportedSources.map(({ name, image }) => (
              <li key={name}>
                <span className="source-brand-icon">
                  <img src={`${assetPrefix}/source-brands/${image}`} alt="" width="32" height="32" />
                </span>
                <span>{name}</span>
              </li>
            ))}
          </ul>
          <p><PenLine size={18} aria-hidden="true" /><span>也支援手動補充會議、討論與工作紀錄。</span></p>
        </section>
        </div>
      </section>

      <section className="workflow-section" id="workflow">
        <div className="page-width workflow-layout">
          <div className="section-label workflow-label"><span>03</span><p>持續回報</p></div>
          <div className="workflow-heading">
            <p className="overline">FROM FIRST NOTE TO A STEADY RHYTHM</p>
            <h2>把回報這件事，<br />放回工作的節奏裡。</h2>
            <p>從第一筆紀錄到定期摘要，WorkLens 把輸入、整理、保護與延續放在同一條流程中，讓你不必在忙碌結束後重新拼湊今天。</p>
          </div>
          <div className="workflow-grid">
            <article><PenLine size={22} /><small>01 / CAPTURE</small><h3>先記下，再慢慢補完</h3><p>工作填寫優先顯示輸入區，未完成內容會暫存為本機草稿；重新開啟也能從原處繼續。</p></article>
            <article><FileText size={22} /><small>02 / SHAPE</small><h3>用固定工作區整理摘要</h3><p>摘要頁將整理方式、AI 整理與儲存放在固定工具列；可套用 Prompt 範本、保留上一版，並隨時還原。</p></article>
            <article><CalendarDays size={22} /><small>03 / CONTINUE</small><h3>讓日常流程自己延續</h3><p>可安排日報、週報與備份；設定也能選擇分類移轉，或以個人同步資料夾交換跨電腦的工作紀錄。</p></article>
          </div>
        </div>
      </section>

      <section className="assistance page-width" id="ai-assistance">
        <div className="section-label"><span>04</span><p>AI 聚焦引擎</p></div>
        <div className="assistance-title">
          <p className="overline">AI FOR WORK DISCOVERY</p>
          <h2>讓 AI 沿著痕跡，<br />找回你真正做過的事。</h2>
        </div>
        <div className="assistance-grid">
          <article><Search size={23} /><small>01 / DISCOVER</small><h3>發掘被忽略的工作</h3><p>從 commit、對話與補充紀錄中辨認同一件工作的關聯，找出你可能沒有意識到的具體推進。</p></article>
          <article><MessageSquareText size={23} /><small>02 / RECONSTRUCT</small><h3>還原完整脈絡</h3><p>把問題、討論、嘗試與決定串在一起，不只看見結果，也看見答案如何一步步形成。</p></article>
          <article><Sparkles size={23} /><small>03 / SYNTHESIZE</small><h3>快速整理成摘要</h3><p>依照你的 Prompt 範本產生日報或週報，將整理時間留給真正需要判斷與創造的工作。</p></article>
        </div>
        <div className="ai-outcome"><span>BEFORE</span><p>「今天好像很忙。」</p><i /><span>WITH WORKLENS AI</span><strong>「我知道自己推進了什麼，也知道它是如何完成的。」</strong></div>
      </section>

      <section className="sensitive-section" id="sensitive-protection">
        <div className="page-width sensitive-layout">
          <div className="section-label sensitive-label"><span>05</span><p>機敏資訊防護</p></div>
          <div className="sensitive-heading">
            <p className="overline">A SAFETY CHECK BEFORE AI</p>
            <h2><span className="heading-line">送出 AI 之前，</span><span className="heading-line">先把機敏資訊留在本機。</span></h2>
            <p>每一次整理前，WorkLens 都會在本機檢查最終工作資料與有效 Prompt。命中內容只會從這次請求的副本遮蔽，原始紀錄不會被改寫。</p>
          </div>

          <div className="sensitive-flow" aria-label="機敏資訊防護流程">
            <article>
              <span className="sensitive-step-number">01 / PREPARE</span>
              <FileText size={22} aria-hidden="true" />
              <h3>整理待送內容</h3>
              <p>將工作資料與本次有效的 Prompt 組成待檢查內容。</p>
            </article>
            <article>
              <span className="sensitive-step-number">02 / SCAN</span>
              <FileSearch size={22} aria-hidden="true" />
              <h3>在本機偵測</h3>
              <p>檢查機敏憑證、台灣個資、Email 與你的自訂敏感詞。</p>
            </article>
            <article>
              <span className="sensitive-step-number">03 / REDACT</span>
              <Eye size={22} aria-hidden="true" />
              <h3>建立遮蔽副本</h3>
              <p>只在記憶體中的請求副本取代命中內容，來源資料維持原樣。</p>
            </article>
            <article>
              <span className="sensitive-step-number">04 / VERIFY</span>
              <ShieldCheck size={22} aria-hidden="true" />
              <h3>複查後才送出</h3>
              <p>遮蔽副本必須再次通過檢查；任何失敗都會停止這次傳送。</p>
            </article>
          </div>

          <div className="sensitive-preview" aria-label="遮蔽後送出的內容範例">
            <div className="sensitive-preview-head"><span><i /> PREPARED REQUEST</span><strong>LOCAL ONLY</strong></div>
            <div className="sensitive-preview-body">
              <p><span>工作摘要</span>協助客戶 Alpha 排查登入異常，完成修正與測試。</p>
              <p><span>聯絡資訊</span><mark>[已遮蔽：個人資料]</mark></p>
              <p><span>存取憑證</span><mark>[已遮蔽：機敏憑證]</mark></p>
              <p><span>專案代號</span><mark>[已遮蔽：自訂敏感詞]</mark></p>
            </div>
            <div className="sensitive-preview-foot"><ShieldCheck size={16} aria-hidden="true" /><span>遮蔽後重新掃描通過，才會交由你指定的 AI 模型處理。</span></div>
          </div>

          <div className="sensitive-notes">
            <article><Eye size={20} aria-hidden="true" /><div><h3>你能先看見再決定</h3><p>手動整理時，WorkLens 會顯示遮蔽後預覽；你可以選擇「遮蔽後送出」或取消。</p></div></article>
            <article><Bot size={20} aria-hidden="true" /><div><h3>排程也使用安全副本</h3><p>排程只會送出通過第二次掃描的遮蔽副本，無法完整檢查時便不傳送。</p></div></article>
          </div>
        </div>
      </section>

      <section className="privacy-section" id="privacy">
        <div className="page-width privacy-layout">
          <div className="section-label light-label"><span>06</span><p>Local-first</p></div>
          <div className="privacy-statement">
            <p className="overline">A PRINCIPLE, NOT A FEATURE</p>
            <h2><span className="heading-line">工作紀錄，</span><span className="heading-line">首先應該屬於</span><span className="heading-line">工作的人。</span></h2>
            <p>WorkLens 完整運行在你的電腦上，不依賴遠端雲端伺服器；工作紀錄與應用服務都留在本機。當你主動使用 AI 時，也會先通過本機的機敏資訊檢查，再依照你的設定交由指定模型處理。</p>
          </div>
          <div className="privacy-rules">
            <article><span><HardDrive size={20} /></span><div><h3>完整運行於本機</h3><p>WorkLens 沒有遠端雲端伺服器；程式、資料與工作紀錄都留在你的裝置上。</p></div></article>
            <article><span><ShieldCheck size={20} /></span><div><h3>只監聽本機位址</h3><p>服務不對區域網路或外部網路開放。</p></div></article>
            <article><span><Database size={20} /></span><div><h3>來源由你選擇</h3><p>第一次啟動不會自動收集任何資料來源。</p></div></article>
            <article><span><Bot size={20} /></span><div><h3>AI 有能力，也有邊界</h3><p>只有主動使用時，選定的內容才會交由你設定的 AI 模型處理。</p></div></article>
          </div>
        </div>
      </section>

      <section className="process-section page-width">
        <div className="process-copy">
          <p className="overline">WHAT REMAINS</p>
          <h2>真正構成工作的，<br />從來不只有完成的那一刻。</h2>
        </div>
        <div className="process-words" aria-label="工作歷程包含">
          <span>尋找</span><i />
          <span>思考</span><i />
          <span>討論</span><i />
          <span>試錯</span><i />
          <span>選擇</span>
        </div>
        <div className="process-note">
          <p>一個問題如何被看見；一個想法如何從模糊輪廓逐漸清晰；一條走不通的路，又如何在嘗試、推翻與重來之後，終於找到向前的方向。</p>
          <p>這些，也都是我們曾經走過的路。</p>
        </div>
      </section>

      <section className="install-section" id="install">
        <div className="page-width install-layout">
          <div className="install-copy">
            <p className="overline">WINDOWS + macOS</p>
            <h2>讓今天的投入，<br />從現在開始有跡可循。</h2>
            <p>支援 Windows 與 macOS，提供一鍵安裝與更新。</p>
            <a className="github-button" href="https://github.com/wengct/WorkLens" target="_blank" rel="noreferrer"><GitFork size={18} /> 在 GitHub 查看 WorkLens</a>
          </div>
          <div className="terminal-card">
            <div className="terminal-head"><span><Terminal size={14} /> QUICK INSTALL</span></div>
            <InstallCommand label="Windows / PowerShell" command={windowsCommand} />
            <InstallCommand label="macOS / Terminal" command={macCommand} />
            <p><Check size={14} /> 自動選擇系統版本並完成健康檢查</p>
          </div>
        </div>
      </section>

      <footer className="site-footer page-width">
        <Brand />
        <blockquote>散落的工作痕跡，我將逐一喚起。<br /><strong>讓每一天的投入，都有跡可循。</strong></blockquote>
      </footer>
    </main>
  );
}
