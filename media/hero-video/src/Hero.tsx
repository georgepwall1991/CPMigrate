import React from 'react';
import {AbsoluteFill, Series} from 'remotion';
import {
  Backdrop,
  FadeIn,
  Heading,
  LogoMark,
  Meter,
  Pill,
  TerminalWindow,
  TypeLine,
} from './components';
import {mono, palette, sans} from './theme';

const SceneTitle: React.FC = () => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
    <FadeIn>
      <LogoMark size={150} />
    </FadeIn>
    <FadeIn delay={8}>
      <div
        style={{
          marginTop: 36,
          fontFamily: sans,
          fontSize: 110,
          fontWeight: 800,
          color: palette.text,
          letterSpacing: -3,
        }}
      >
        CPMigrate
      </div>
    </FadeIn>
    <FadeIn delay={16}>
      <div
        style={{
          marginTop: 18,
          fontFamily: sans,
          fontSize: 38,
          color: palette.muted,
        }}
      >
        NuGet Central Package Management for .NET — without the hand-editing
      </div>
    </FadeIn>
    <div style={{display: 'flex', gap: 18, marginTop: 46}}>
      <Pill label="Migrate" delay={26} />
      <Pill label="Analyze" delay={32} />
      <Pill label="Auto-fix" delay={38} />
      <Pill label="Update + Rollback" delay={44} />
    </div>
  </AbsoluteFill>
);

const ConflictRow: React.FC<{
  delay: number;
  pkg: string;
  versions: string;
  resolved: string;
}> = ({delay, pkg, versions, resolved}) => (
  <FadeIn delay={delay} y={8}>
    <div style={{display: 'flex', fontFamily: mono, fontSize: 28, lineHeight: 1.9}}>
      <div style={{width: 380, color: palette.text}}>{pkg}</div>
      <div style={{width: 380, color: palette.dim}}>{versions}</div>
      <div style={{color: palette.cyan}}>➜ {resolved}</div>
    </div>
  </FadeIn>
);

const SceneMigrate: React.FC = () => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
    <div style={{marginBottom: 34}}>
      <Heading>Generate Directory.Packages.props in one command</Heading>
    </div>
    <FadeIn delay={4}>
      <TerminalWindow title="cpmigrate — migration dry run" width={1380}>
        <TypeLine text="cpmigrate -s ./MySolution.sln --dry-run" start={8} cps={26} size={32} />
        <div style={{height: 34}} />
        <FadeIn delay={58}>
          <div
            style={{
              fontFamily: mono,
              fontSize: 22,
              letterSpacing: 4,
              color: palette.dim,
              marginBottom: 14,
            }}
          >
            VERSION CONFLICTS
          </div>
        </FadeIn>
        <ConflictRow delay={66} pkg="Newtonsoft.Json" versions="13.0.3, 13.0.1" resolved="13.0.3" />
        <ConflictRow delay={76} pkg="Serilog" versions="3.1.1, 3.0.0" resolved="3.1.1" />
        <ConflictRow delay={86} pkg="Polly" versions="8.4.1, 8.2.0" resolved="8.4.1" />
        <div style={{height: 26}} />
        <FadeIn delay={100}>
          <div style={{fontFamily: mono, fontSize: 28, color: palette.green}}>
            ✔ Generated Directory.Packages.props{' '}
            <span style={{color: palette.dim}}>(dry run — 0 files modified)</span>
          </div>
        </FadeIn>
      </TerminalWindow>
    </FadeIn>
  </AbsoluteFill>
);

const SceneRisk: React.FC = () => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
    <div style={{marginBottom: 34}}>
      <Heading delay={0}>Know the blast radius before you commit</Heading>
    </div>
    <FadeIn delay={6}>
      <div
        style={{
          width: 1080,
          borderRadius: 18,
          border: `1px solid ${palette.cardBorder}`,
          background: palette.card,
          boxShadow: '0 40px 120px rgba(0, 0, 0, 0.55)',
          padding: '44px 56px',
          fontFamily: mono,
        }}
      >
        <div style={{fontSize: 22, letterSpacing: 4, color: palette.dim, marginBottom: 26}}>
          ASSESSMENT
        </div>
        <div style={{display: 'flex', alignItems: 'center', gap: 26, marginBottom: 22}}>
          <span style={{fontSize: 30, color: palette.text}}>Migration Risk</span>
          <Meter value={58} start={16} />
          <span style={{fontSize: 30, color: palette.amber, fontWeight: 700}}>HIGH 58/100</span>
        </div>
        <FadeIn delay={34}>
          <div style={{fontSize: 27, color: palette.blue, marginBottom: 12}}>
            Impact Area: 12 projects • 7 conflicting packages
          </div>
        </FadeIn>
        <FadeIn delay={44}>
          <div style={{fontSize: 27, color: palette.muted}}>
            Assessment: Significant version conflicts. Review recommended.
          </div>
        </FadeIn>
      </div>
    </FadeIn>
  </AbsoluteFill>
);

const BisectRow: React.FC<{
  delay: number;
  kind: 'HELD' | 'APPLIED';
  text: string;
}> = ({delay, kind, text}) => (
  <FadeIn delay={delay} y={8}>
    <div style={{display: 'flex', gap: 22, fontFamily: mono, fontSize: 29, lineHeight: 1.9}}>
      <span style={{width: 150, color: kind === 'HELD' ? palette.amber : palette.green}}>
        {kind}
      </span>
      <span style={{color: kind === 'HELD' ? palette.text : palette.muted}}>{text}</span>
    </div>
  </FadeIn>
);

const SceneBisect: React.FC = () => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
    <div style={{marginBottom: 34}}>
      <Heading>Update packages. Keep the largest green subset.</Heading>
    </div>
    <FadeIn delay={4}>
      <TerminalWindow title="cpmigrate --update-packages --bisect" width={1380}>
        <BisectRow delay={14} kind="HELD" text="Serilog      3.1.1 → 4.2.0   (tests failed)" />
        <BisectRow delay={24} kind="HELD" text="AutoMapper  12.0.1 → 14.0.0  (tests failed)" />
        <BisectRow delay={34} kind="APPLIED" text="Polly          8.4.1 → 8.6.4" />
        <BisectRow delay={42} kind="APPLIED" text="FluentValidation  11.9.0 → 12.1.1" />
        <BisectRow delay={50} kind="APPLIED" text="+ 32 more applied" />
        <div style={{height: 24}} />
        <FadeIn delay={64}>
          <div style={{fontFamily: mono, fontSize: 31, color: palette.green}}>
            ✔ Kept 36/38 updates with tests green{' '}
            <span style={{color: palette.dim}}>(9 verification runs)</span>
          </div>
        </FadeIn>
      </TerminalWindow>
    </FadeIn>
  </AbsoluteFill>
);

const SceneCta: React.FC = () => (
  <AbsoluteFill style={{justifyContent: 'center', alignItems: 'center'}}>
    <FadeIn>
      <div style={{display: 'flex', alignItems: 'center', gap: 28}}>
        <LogoMark size={104} />
        <span
          style={{fontFamily: sans, fontSize: 72, fontWeight: 800, color: palette.text, letterSpacing: -2}}
        >
          CPMigrate
        </span>
      </div>
    </FadeIn>
    <FadeIn delay={10}>
      <div
        style={{
          marginTop: 46,
          padding: '26px 48px',
          borderRadius: 16,
          border: `1px solid ${palette.cardBorder}`,
          background: palette.card,
          fontFamily: mono,
          fontSize: 40,
          color: palette.text,
        }}
      >
        <span style={{color: palette.dim}}>$ </span>
        dotnet tool install --global CPMigrate
      </div>
    </FadeIn>
    <FadeIn delay={20}>
      <div style={{marginTop: 34, fontFamily: mono, fontSize: 30, color: palette.cyan}}>
        georgepwall1991.github.io/CPMigrate
      </div>
    </FadeIn>
    <FadeIn delay={28}>
      <div style={{marginTop: 16, fontFamily: sans, fontSize: 26, color: palette.muted}}>
        Migrate · Analyze · Auto-fix · Update · Rollback
      </div>
    </FadeIn>
  </AbsoluteFill>
);

export const Hero: React.FC = () => (
  <AbsoluteFill style={{backgroundColor: palette.bg}}>
    <Backdrop />
    <Series>
      <Series.Sequence durationInFrames={140}>
        <SceneTitle />
      </Series.Sequence>
      <Series.Sequence durationInFrames={160}>
        <SceneMigrate />
      </Series.Sequence>
      <Series.Sequence durationInFrames={110}>
        <SceneRisk />
      </Series.Sequence>
      <Series.Sequence durationInFrames={120}>
        <SceneBisect />
      </Series.Sequence>
      <Series.Sequence durationInFrames={70}>
        <SceneCta />
      </Series.Sequence>
    </Series>
  </AbsoluteFill>
);
