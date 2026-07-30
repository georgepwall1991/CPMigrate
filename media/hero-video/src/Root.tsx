import React from 'react';
import {Composition} from 'remotion';
import {Hero} from './Hero';
import {SocialCard} from './SocialCard';

export const RemotionRoot: React.FC = () => (
  <>
    <Composition
      id="Hero"
      component={Hero}
      durationInFrames={600}
      fps={30}
      width={1920}
      height={1080}
    />
    <Composition
      id="SocialCard"
      component={SocialCard}
      durationInFrames={1}
      fps={30}
      width={1200}
      height={630}
    />
  </>
);
