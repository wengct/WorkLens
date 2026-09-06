import {
  ArrowRight,
  Bot,
  CalendarDays,
  Check,
  Code2,
  Database,
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
          <a href="#privacy">Local-first</a>
        </nav>
        <a className="header-action" href="#install">下載使用 <ArrowRight size={16} /></a>
      </header>

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
          <p>今天，我究竟<br />完成了什麼？</p>
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
            <div className="shot-toolbar"><span><i /> PRODUCT VIEW</span><span>工作摘要・每週歷程</span></div>
            <img src={`${assetPrefix}/worklens-summary.png`} alt="WorkLens 工作摘要畫面，顯示每週工作歷程、每日明細與可編輯的每日摘要" width="1920" height="989" />
            <figcaption><span>01</span> 從整週概覽、每日明細到完整摘要，所有工作脈絡都在同一個畫面裡。</figcaption>
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

      <section className="assistance page-width">
        <div className="section-label"><span>03</span><p>AI 聚焦引擎</p></div>
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

      <section className="privacy-section" id="privacy">
        <div className="page-width privacy-layout">
          <div className="section-label light-label"><span>04</span><p>Local-first</p></div>
          <div className="privacy-statement">
            <p className="overline">A PRINCIPLE, NOT A FEATURE</p>
            <h2><span className="heading-line">工作紀錄，</span><span className="heading-line">首先應該屬於</span><span className="heading-line">工作的人。</span></h2>
            <p>WorkLens 完整運行在你的電腦上，不依賴遠端雲端伺服器；工作紀錄與應用服務都留在本機。只有當你主動使用 AI 時，才會依照你的設定，交由你指定的 AI 模型處理。</p>
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
