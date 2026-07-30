import React from 'react';
import {interpolate, spring, useCurrentFrame, useVideoConfig} from 'remotion';
import {mono, palette, sans} from './theme';

export const Backdrop: React.FC = () => (
  <div
    style={{
      position: 'absolute',
      inset: 0,
      background: `linear-gradient(160deg, ${palette.bg} 0%, #0c1220 55%, #0a0f1a 100%)`,
    }}
  >
    <div
      style={{
        position: 'absolute',
        width: 900,
        height: 900,
        top: -320,
        right: -180,
        borderRadius: '50%',
        background: `radial-gradient(circle, ${palette.glowMagenta}, transparent 65%)`,
      }}
    />
    <div
      style={{
        position: 'absolute',
        width: 800,
        height: 800,
        bottom: -300,
        left: -160,
        borderRadius: '50%',
        background: `radial-gradient(circle, ${palette.glowCyan}, transparent 65%)`,
      }}
    />
  </div>
);

export const FadeIn: React.FC<{
  delay?: number;
  y?: number;
  children: React.ReactNode;
}> = ({delay = 0, y = 18, children}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const progress = spring({frame: frame - delay, fps, config: {damping: 200}});
  return (
    <div
      style={{
        opacity: progress,
        transform: `translateY(${interpolate(progress, [0, 1], [y, 0])}px)`,
      }}
    >
      {children}
    </div>
  );
};

export const LogoMark: React.FC<{size?: number}> = ({size = 96}) => (
  <div
    style={{
      width: size,
      height: size,
      borderRadius: size * 0.24,
      background: `linear-gradient(135deg, ${palette.magenta}, #b5179e 55%, #7c3aed)`,
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      boxShadow: '0 12px 48px rgba(255, 45, 149, 0.35)',
    }}
  >
    <svg width={size * 0.56} height={size * 0.56} viewBox="0 0 24 24" fill="none">
      <path
        d="M12 2 3 7v10l9 5 9-5V7l-9-5Z"
        stroke="white"
        strokeWidth="1.8"
        strokeLinejoin="round"
      />
      <path d="M3 7l9 5 9-5M12 12v10" stroke="white" strokeWidth="1.8" strokeLinejoin="round" />
      <circle cx="12" cy="12" r="2.4" fill="white" />
    </svg>
  </div>
);

export const Pill: React.FC<{label: string; delay?: number}> = ({label, delay = 0}) => (
  <FadeIn delay={delay} y={10}>
    <div
      style={{
        padding: '10px 22px',
        borderRadius: 999,
        border: `1px solid ${palette.cardBorder}`,
        background: 'rgba(15, 21, 29, 0.7)',
        color: palette.cyan,
        fontFamily: mono,
        fontSize: 22,
        whiteSpace: 'nowrap',
      }}
    >
      {label}
    </div>
  </FadeIn>
);

export const TypeLine: React.FC<{
  text: string;
  start: number;
  cps?: number;
  size?: number;
  color?: string;
}> = ({text, start, cps = 24, size = 30, color = palette.text}) => {
  const frame = useCurrentFrame();
  const elapsed = Math.max(0, frame - start);
  const chars = Math.min(text.length, Math.floor((elapsed * cps) / 30));
  const done = chars >= text.length;
  return (
    <div style={{fontFamily: mono, fontSize: size, color, whiteSpace: 'pre', lineHeight: 1.5}}>
      <span style={{color: palette.dim}}>{'> '}</span>
      {text.slice(0, chars)}
      {!done && frame % 16 < 10 ? <span style={{color: palette.cyan}}>▍</span> : null}
    </div>
  );
};

export const Meter: React.FC<{value: number; start: number; width?: number}> = ({
  value,
  start,
  width = 560,
}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const p = spring({frame: frame - start, fps, config: {damping: 200}});
  const fill = (value / 100) * p;
  return (
    <div
      style={{
        width,
        height: 26,
        borderRadius: 13,
        background: '#1a2330',
        border: `1px solid ${palette.cardBorder}`,
        padding: 3,
      }}
    >
      <div
        style={{
          width: `${fill * 100}%`,
          height: '100%',
          borderRadius: 10,
          background: `linear-gradient(90deg, ${palette.amber}, ${palette.magenta})`,
        }}
      />
    </div>
  );
};

export const TerminalWindow: React.FC<{
  title: string;
  width?: number;
  children: React.ReactNode;
}> = ({title, width = 1320, children}) => (
  <div
    style={{
      width,
      borderRadius: 18,
      border: `1px solid ${palette.cardBorder}`,
      background: palette.card,
      boxShadow: '0 40px 120px rgba(0, 0, 0, 0.55)',
      overflow: 'hidden',
    }}
  >
    <div
      style={{
        height: 54,
        background: palette.chrome,
        borderBottom: `1px solid ${palette.cardBorder}`,
        display: 'flex',
        alignItems: 'center',
        padding: '0 22px',
        gap: 10,
      }}
    >
      <div style={{width: 14, height: 14, borderRadius: 7, background: '#ff5f56'}} />
      <div style={{width: 14, height: 14, borderRadius: 7, background: '#ffbd2e'}} />
      <div style={{width: 14, height: 14, borderRadius: 7, background: '#27c93f'}} />
      <div style={{flex: 1, textAlign: 'center', fontFamily: mono, fontSize: 18, color: palette.dim}}>
        {title}
      </div>
      <div style={{width: 52}} />
    </div>
    <div style={{padding: '30px 40px 40px'}}>{children}</div>
  </div>
);

export const Heading: React.FC<{children: React.ReactNode; delay?: number}> = ({
  children,
  delay = 0,
}) => (
  <FadeIn delay={delay}>
    <div
      style={{
        fontFamily: sans,
        fontSize: 52,
        fontWeight: 700,
        color: palette.text,
        letterSpacing: -1,
      }}
    >
      {children}
    </div>
  </FadeIn>
);
