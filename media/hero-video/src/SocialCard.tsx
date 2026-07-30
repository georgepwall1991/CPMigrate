import React from 'react';
import {AbsoluteFill} from 'remotion';
import {Backdrop, LogoMark} from './components';
import {mono, palette, sans} from './theme';

const CardPill: React.FC<{label: string}> = ({label}) => (
  <div
    style={{
      padding: '8px 18px',
      borderRadius: 999,
      border: `1px solid ${palette.cardBorder}`,
      background: 'rgba(15, 21, 29, 0.75)',
      color: palette.cyan,
      fontFamily: mono,
      fontSize: 19,
    }}
  >
    {label}
  </div>
);

export const SocialCard: React.FC = () => (
  <AbsoluteFill style={{backgroundColor: palette.bg}}>
    <Backdrop />
    <AbsoluteFill style={{padding: 64, justifyContent: 'space-between'}}>
      <div style={{display: 'flex', alignItems: 'center', gap: 22}}>
        <LogoMark size={72} />
        <span style={{fontFamily: sans, fontSize: 52, fontWeight: 800, color: palette.text, letterSpacing: -1.5}}>
          CPMigrate
        </span>
        <span
          style={{
            marginLeft: 10,
            padding: '6px 16px',
            borderRadius: 999,
            border: `1px solid ${palette.cardBorder}`,
            color: palette.muted,
            fontFamily: mono,
            fontSize: 18,
          }}
        >
          .NET global tool
        </span>
      </div>

      <div>
        <div style={{fontFamily: sans, fontSize: 56, fontWeight: 800, color: palette.text, letterSpacing: -1.5, lineHeight: 1.15}}>
          NuGet Central Package Management,
          <br />
          <span style={{color: palette.magenta}}>without the hand-editing.</span>
        </div>
        <div style={{marginTop: 18, fontFamily: sans, fontSize: 26, color: palette.muted}}>
          Migrate to Directory.Packages.props · Analyze dependency health · Update packages with
          rollback
        </div>
      </div>

      <div style={{display: 'flex', alignItems: 'center', justifyContent: 'space-between'}}>
        <div style={{display: 'flex', gap: 12}}>
          <CardPill label="Migrate" />
          <CardPill label="Analyze" />
          <CardPill label="Auto-fix" />
          <CardPill label="--bisect" />
        </div>
        <div
          style={{
            padding: '14px 28px',
            borderRadius: 12,
            border: `1px solid ${palette.cardBorder}`,
            background: palette.card,
            fontFamily: mono,
            fontSize: 23,
            color: palette.text,
          }}
        >
          <span style={{color: palette.dim}}>$ </span>
          dotnet tool install --global CPMigrate
        </div>
      </div>
    </AbsoluteFill>
  </AbsoluteFill>
);
