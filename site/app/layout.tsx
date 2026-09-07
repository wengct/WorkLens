import type { Metadata } from 'next';
import './globals.css';

const siteUrl = 'https://wengct.github.io/WorkLens/';

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: 'WorkLens — 讓每一天的投入，都有跡可循',
  description: '以 local-first 為核心，讓 AI 從 Git、Codex 與日常紀錄中發掘工作、還原脈絡；送出前先在本機遮蔽機敏資訊。',
  openGraph: {
    title: 'WorkLens — 讓每一天的投入，都有跡可循',
    description: '讓 AI 沿著散落的工作痕跡，找回你真正做過的事；機敏資訊先在本機檢查與遮蔽。',
    type: 'website',
    locale: 'zh_TW',
    url: siteUrl,
  },
  twitter: {
    card: 'summary',
    title: 'WorkLens — 讓每一天的投入，都有跡可循',
    description: '讓 AI 沿著散落的工作痕跡，找回你真正做過的事；機敏資訊先在本機檢查與遮蔽。',
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="zh-Hant-TW"><body>{children}</body></html>;
}
