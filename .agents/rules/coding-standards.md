# STANDARDS DE CODE & BONNES PRATIQUES

## 1. Environnement & Langage
- **Target Framework** : .NET 10 (`net10.0`).
- **Langage** : C# 14 avec les fonctionnalités modernes (pattern matching avancé, primary constructors, collection expressions `[...]`, file-scoped namespaces, required properties).
- **Nullable Reference Types** : Toujours activé (`<Nullable>enable</Nullable>`). Aucun avertissement d'absence de vérification null toléré.

## 2. Performance & Gestion Mémoire (Traitement Haute Vitesse)
Le traitement astrophotographique manipule des millions d'images 16-bit et des dizaines de gigaoctets de données :
- **Zero-Allocation dans la boucle critique** :
  - Pas d'allocations de gros tableaux (`ushort[]` ou `byte[]`) à chaque frame.
  - Utiliser `ArrayPool<T>.Shared` ou des buffers réutilisables pour le streaming.
  - Privilégier `Span<T>`, `ReadOnlySpan<T>` et `Memory<T>`.
- **Min-Heap borné** :
  - Conserver uniquement les $K$ meilleures frames en mémoire sans matérialiser l'ensemble de la séquence vidéo.
- **Pointeurs unsafe** :
  - L'usage de blocs `unsafe` avec pointeurs bruts (`ushort*`) est autorisé et recommandé pour les boucles de calcul intensif de pixels (centrage, débayérisation, convolution laplacienne).

## 3. Qualité & Clean Code
- **SOLID & Séparation des Responsabilités** :
  - Classes courtes, focalisées sur une seule responsabilité.
  - Aucune dépendance cyclique entre projets.
- **Documentation & Naming** :
  - Noms explicites en anglais pour le code technique (`CentroidDetector`, `DebayerFilter`).
  - Documentation XML (`/// <summary>`) sur toutes les interfaces de ports et types du domaine.
  - Préserver les commentaires et docstrings existants lors des modifications.

## 4. Tests Unitaires & Fiabilité
- Framework : xUnit.
- Tout nouveau composant du domaine ou cas d'utilisation doit être couvert par des tests unitaires déterministes.
- Utiliser le `SyntheticAstroGenerator` pour tester les algorithmes d'image sans dépendre de gros fichiers externes.
