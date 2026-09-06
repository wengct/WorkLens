'use client';

import { useEffect, useRef, useState } from 'react';
import { Check, Copy } from 'lucide-react';
import { Button } from '@/components/ui/button';

export function InstallCommand({ label, command }: { label: string; command: string }) {
  const [status, setStatus] = useState<'idle' | 'copied' | 'error'>('idle');
  const resetTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (resetTimer.current) clearTimeout(resetTimer.current);
  }, []);

  async function copyCommand() {
    if (resetTimer.current) clearTimeout(resetTimer.current);
    try {
      await navigator.clipboard.writeText(command);
      setStatus('copied');
      resetTimer.current = setTimeout(() => setStatus('idle'), 2500);
    } catch {
      setStatus('error');
    }
  }

  return (
    <div className="terminal-command">
      <span>{label}</span>
      <div className="terminal-command-row">
        <code><i aria-hidden="true">$</i> {command}</code>
        <Button
          type="button"
          variant="ghost"
          size="icon"
          className="terminal-copy"
          onClick={copyCommand}
          aria-label={`複製 ${label} 安裝指令`}
          title={status === 'copied' ? '已複製' : '複製指令'}
        >
          {status === 'copied' ? <Check aria-hidden="true" /> : <Copy aria-hidden="true" />}
        </Button>
      </div>
      <span className="terminal-copy-status" role="status">
        {status === 'copied' ? '已複製' : status === 'error' ? '無法存取剪貼簿，請選取指令手動複製。' : ''}
      </span>
    </div>
  );
}
