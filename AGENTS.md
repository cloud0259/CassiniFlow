# DIRECTIVES AGENT & CONTEXTE — CASSINIFLOW

Ce fichier et le dossier `.agents/rules/` définissent le contexte métier, les exigences d'architecture et les règles de développement pour le projet **CassiniFlow**.

## 1. Contexte du Projet
Consulter [`CONTEXT.md`](file:///e:/CassiniFlow/CONTEXT.md) pour la description complète du projet, les fonctionnalités déjà implémentées (codecs SER/FITS/vidéo, détection de planète, tri qualité, interface Avalonia, CLI).

## 2. Règles d'Architecture Obligatoires
Consulter les fichiers de règles détaillés :
- [Architecture Hexagonale, DDD et Design Patterns](file:///e:/CassiniFlow/.agents/rules/architecture.md) :
  - Découpage strict en couches : **Domain** (`CassiniFlow.Domain`, zéro dépendance UI/infra), **Application** (`CassiniFlow.Application`, use cases et pipeline steps), **Infrastructure** (`CassiniFlow.Infrastructure`, adaptateurs secondaires SER, FITS, OpenCV, FFmpeg), **Presentation** (`CassiniFlow.Desktop` et `CassiniFlow.Cli`).
  - Design Patterns à appliquer : Strategy (débayérisation, scoring qualité, centrage), Factory (readers/writers), Pipeline (étapes de traitement d'image composables), Adapter (OpenCV/FFmpeg/fichiers).
- [Standards de Code & Bonnes Pratiques](file:///e:/CassiniFlow/.agents/rules/coding-standards.md) :
  - .NET 10 / C# 14, `<Nullable>enable</Nullable>`.
  - Zero-allocation sur les boucles de traitement de frames (Spans, ArrayPool, unsafe pointers).
- [Workflow Git & Dépôt](file:///e:/CassiniFlow/.agents/rules/git-workflow.md) :
  - Conventional Commits, exclusion systématique des fichiers volumineux (`.ser`, `.fit`, `publish/`, `cassiniflow_output/`).
