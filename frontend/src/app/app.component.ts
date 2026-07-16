import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { environment } from '../environments/environment';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  readonly title = 'SiteGuardian';
  readonly phase = 'Phase 0 — squelette';
  readonly apiBaseUrl = environment.apiBaseUrl;
  readonly demo = environment.demo;
}
