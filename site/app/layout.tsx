import type { Metadata } from 'next';
import './globals.css';

const siteUrl = 'https://wengct.github.io/WorkLens/';

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: 'WorkLens — 讓每一天的投入，都有跡可循',
  description: '以 local-first 為核心，從工作紀錄、AI 對話到日報與週報，讓 AI 重整工作脈絡；支援草稿、範本、排程與本機機敏遮蔽。',
  openGraph: {
    title: 'WorkLens — 讓每一天的投入，都有跡可循',
    description: '從零散工作痕跡到穩定的日報與週報流程；機敏資訊先在本機檢查與遮蔽。',
    type: 'website',
    locale: 'zh_TW',
    url: siteUrl,
  },
  twitter: {
    card: 'summary',
    title: 'WorkLens — 讓每一天的投入，都有跡可循',
    description: '從零散工作痕跡到穩定的日報與週報流程；機敏資訊先在本機檢查與遮蔽。',
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="zh-Hant-TW"><body>{children}</body></html>;
}
